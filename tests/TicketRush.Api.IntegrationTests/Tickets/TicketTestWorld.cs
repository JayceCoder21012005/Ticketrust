using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary>A paid order's ticket as issued by the test, with the seat it took (null for general admission).</summary>
public sealed record IssuedTicket(Guid Id, Guid OrderId, Guid EventId, int? SeatIndex);

/// <summary>
/// The api over real containers with the dev seed, plus helpers that put paid orders and their tickets in the database the
/// way the order tests do (domain model: <c>Order.Create → StartPayment → MarkPaid</c> and <c>Ticket.Issue</c>), and move an
/// event or ticket to a state the test needs.
/// </summary>
public sealed class TicketTestWorld : IAsyncDisposable
{
    public const string SeatedSlug = "son-tung-mtp-live";
    public const string GaSlug = "vu-cat-tuong-inner-space";
    public const string OtherGaSlug = "den-vau-show-2026";
    public const string ThirdGaSlug = "my-tam-tri-ky";

    private readonly Dictionary<Guid, int> _seatsUsed = [];

    private TicketTestWorld(ApiFactory factory)
    {
        Factory = factory;
        Client = AuthApi.CreateClient(factory);
        Auth = new AuthApi(factory, Client);
    }

    public sealed record User(Guid Id, string Access, string FullName);

    public ApiFactory Factory { get; }

    public HttpClient Client { get; }

    public AuthApi Auth { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TicketTestWorld> CreateAsync(ContainersFixture containers, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: settings);
        var world = new TicketTestWorld(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        return world;
    }

    public async Task<User> NewUserAsync(string fullName = "Nguyễn Văn Đạt")
    {
        var email = $"holder-{Guid.NewGuid():N}@example.com";
        var id = Guid.CreateVersion7();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = id, Email = email, UserName = email, FullName = fullName, EmailConfirmed = true };
            (await users.CreateAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, "Customer")).Succeeded.ShouldBeTrue();
        }

        var login = await LoginResult.ReadAsync(await Auth.LoginAsync(email));
        return new User(id, login.AccessToken, fullName);
    }

    public async Task<Guid> EventIdAsync(string slug) =>
        await WithDbAsync(db => db.Events.AsNoTracking().Where(e => e.Slug == slug).Select(e => e.Id).SingleAsync(Ct));

    /// <summary>One paid order with one seated ticket per seat, taking the event's next free seats in index order.</summary>
    public async Task<IReadOnlyList<IssuedTicket>> IssueSeatedAsync(User owner, string slug, int count)
    {
        var eventId = await EventIdAsync(slug);
        var skip = _seatsUsed.GetValueOrDefault(eventId);
        _seatsUsed[eventId] = skip + count;
        return await WithDbAsync(async db =>
        {
            var seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == eventId).OrderBy(s => s.Index).Skip(skip).Take(count).ToListAsync(Ct);
            var prices = await PricesAsync(db, eventId);
            var items = seats.Select(s => new OrderItem(s.TierId, s.Index, prices[s.TierId], 1)).ToList();
            return await IssueAsync(db, owner, eventId, items);
        });
    }

    /// <summary>One paid order with <paramref name="quantity"/> general-admission tickets of the event's first GA tier.</summary>
    public async Task<IReadOnlyList<IssuedTicket>> IssueGaAsync(User owner, string slug, int quantity = 1)
    {
        var eventId = await EventIdAsync(slug);
        return await WithDbAsync(async db =>
        {
            var tier = await db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Tiers)
                .Where(t => t.Kind == TicketKind.GeneralAdmission).OrderBy(t => t.Name).FirstAsync(Ct);
            return await IssueAsync(db, owner, eventId, [new OrderItem(tier.Id, null, tier.Price, quantity)]);
        });
    }

    public Task SetEventAsync(Guid eventId, EventStatus? status = null, DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null) =>
        WithDbAsync(async db =>
        {
            if (status is { } s)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE events SET status = {s.ToString()} WHERE id = {eventId}", Ct);
            }

            if (startsAt is { } start)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE events SET starts_at = {start} WHERE id = {eventId}", Ct);
            }

            if (endsAt is { } end)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE events SET ends_at = {end} WHERE id = {eventId}", Ct);
            }

            return 0;
        });

    public Task SetTicketStatusAsync(Guid ticketId, TicketStatus status) =>
        WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE tickets SET status = {status.ToString()} WHERE id = {ticketId}", Ct));

    /// <summary>The ticket's secret as the application sees it (decrypted by the EF converter).</summary>
    public Task<byte[]> SecretOfAsync(Guid ticketId) =>
        WithDbAsync(db => db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.QrSecret).SingleAsync(Ct));

    public Task<HttpResponseMessage> GetAsync(string path, User? who)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Access);
        }

        return Client.SendAsync(request, Ct);
    }

    public async Task<JsonElement> JsonAsync(string path, User? who, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        await ReadAsync(await GetAsync(path, who), status);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    private static async Task<Dictionary<Guid, Money>> PricesAsync(AppDbContext db, Guid eventId) =>
        (await db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Tiers).ToListAsync(Ct)).ToDictionary(t => t.Id, t => t.Price);

    private static async Task<IReadOnlyList<IssuedTicket>> IssueAsync(AppDbContext db, User owner, Guid eventId, List<OrderItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        var total = items.Aggregate(Money.Zero(), (sum, item) => sum + item.LineTotal());
        var order = Order.Create(owner.Id, eventId, Guid.CreateVersion7(), items, total, new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        order.MarkPaid(now);
        db.Orders.Add(order);
        var tickets = items
            .SelectMany(i => Enumerable.Range(0, i.Quantity).Select(_ => Ticket.Issue(order.Id, eventId, i.TierId, i.SeatIndex, owner.Id)))
            .ToList();
        db.Tickets.AddRange(tickets);
        await db.SaveChangesAsync(Ct);
        return [.. tickets.Select(t => new IssuedTicket(t.Id, order.Id, eventId, t.SeatIndex))];
    }
}

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Catalog;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Persistence;

/// <param name="options">EF options.</param>
/// <param name="dataProtection">
/// Encrypts ticket QR secrets at rest. Optional only so design-time and migration-only contexts can be built without
/// Data Protection; saving or reading a ticket without it throws.
/// </param>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IDataProtectionProvider? dataProtection = null)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    /// <summary>Shadow property mapped to Postgres' system column <c>xmin</c> (optimistic concurrency).</summary>
    public const string RowVersionProperty = "Version";

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<EventSeat> EventSeats => Set<EventSeat>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<ProcessedWebhook> ProcessedWebhooks => Set<ProcessedWebhook>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ConsumerReceipt> ConsumerReceipts => Set<ConsumerReceipt>();

    /// <summary>Part of the model cache key: the converter below captures this provider's protector.</summary>
    internal IDataProtectionProvider? DataProtection => dataProtection;

    Task<int> IAppDbContext.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    /// <inheritdoc />
    /// <remarks>Retries are disabled (<c>DisableRetry</c>), so today the strategy runs the work exactly once.</remarks>
    public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => RunInTransactionAsync(work, null, ct);

    /// <inheritdoc />
    public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan lockTimeout, CancellationToken ct) =>
        RunInTransactionAsync(work, lockTimeout, ct);

    /// <summary>Deadlocks and serialization failures become <see cref="DatabaseContentionException"/> here, for every use case.</summary>
    private Task<T> RunInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan? lockTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                try
                {
                    // Disposing without a commit rolls back.
                    await using var transaction = await Database.BeginTransactionAsync(token);
                    await SetLockTimeoutAsync(lockTimeout, token);
                    var result = await work(token);
                    await transaction.CommitAsync(token);
                    return result;
                }
                catch (Exception ex) when (PostgresErrors.IsContention(ex))
                {
                    throw new DatabaseContentionException("The transaction collided with a concurrent one and was rolled back.", ex);
                }
                catch (Exception ex) when (PostgresErrors.IsLockTimeout(ex))
                {
                    throw new DatabaseLockTimeoutException("A lock wait went over the transaction's lock timeout; it was rolled back.", ex);
                }
            },
            ct);
    }

    private Task SetLockTimeoutAsync(TimeSpan? lockTimeout, CancellationToken ct)
    {
        if (lockTimeout is not { } timeout)
        {
            return Task.CompletedTask;
        }

        // SET does not take parameters; the value is an integer we format ourselves.
        var milliseconds = Math.Max(1, (long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
#pragma warning disable EF1002 // Not user input: a formatted integer.
        return Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{milliseconds}ms'", ct);
#pragma warning restore EF1002
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("unaccent").HasPostgresExtension("pg_trgm");
        builder.HasDbFunction(typeof(CatalogDbFunctions).GetMethod(nameof(CatalogDbFunctions.Matches))!).HasName("catalog_matches");
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        RenameIdentityTables(builder);
        MapRowVersionOnAggregates(builder);
        MapTicketSecretEncryption(builder);
    }

    private void MapTicketSecretEncryption(ModelBuilder builder) =>
        builder.Entity<Ticket>().Property(t => t.QrSecret).HasConversion(new ProtectedBytesConverter(TicketSecretProtector.Create(dataProtection)));

    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>().ToTable("users").Ignore(u => u.DomainEvents);
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }

    private static void MapRowVersionOnAggregates(ModelBuilder builder)
    {
        var aggregates = builder.Model.GetEntityTypes()
            .Where(type => type.ClrType.IsAssignableTo(typeof(AggregateRoot)) && !type.IsOwned())
            .Select(type => type.ClrType)
            .ToList();

        foreach (var clrType in aggregates)
        {
            builder.Entity(clrType)
                .Property<uint>(RowVersionProperty)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
    }
}

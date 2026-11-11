using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Records every email, optionally refusing some addresses and slowing every send down (to widen race windows).</summary>
public sealed class ScriptedEmailSender : IEmailSender
{
    private int _attempts;

    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public ConcurrentBag<string> RefusedAddresses { get; } = [];

    /// <summary>Sends to these addresses never finish by themselves (a dropped SMTP connection): they end only when cancelled.</summary>
    public ConcurrentBag<string> HangingAddresses { get; } = [];

    /// <summary>Released each time a send to a hanging address starts.</summary>
    public SemaphoreSlim HangStarted { get; } = new(0);

    public TimeSpan Delay { get; set; }

    /// <summary>Every call, including the refused ones.</summary>
    public int Attempts => _attempts;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref _attempts);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (HangingAddresses.Contains(message.To))
        {
            HangStarted.Release();
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (RefusedAddresses.Contains(message.To))
        {
            throw new InvalidOperationException($"550 mailbox {message.To} unavailable");
        }

        Sent.Enqueue(message);
    }
}

/// <summary>Collects log lines so a test can assert what was (not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(Lines);

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{formatter(state, exception)} {exception}");
    }
}

/// <summary>A worker-like host for the notification tests: real infrastructure, scripted mail sender, controllable clock.</summary>
public sealed class NotificationTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly IHost _host;

    public NotificationTestHost(string database, string redis, ScriptedEmailSender sender, FakeTimeProvider clock, int batchSize = 20, CapturingLoggerProvider? logs = null, TimeSpan? sendTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(database, redis));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.AddInfrastructure();
        builder.Services.AddApplication();
        builder.Services.AddSingleton<IEmailSender>(sender);
        builder.Services.Configure<EmailDeliveryOptions>(options =>
        {
            options.BatchSize = batchSize;
            options.SendTimeout = sendTimeout ?? options.SendTimeout;
        });
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        _host = builder.Build();
    }

    public EmailDeliveryProcessor Processor => _host.Services.GetRequiredService<EmailDeliveryProcessor>();

    public AsyncServiceScope Scope() => _host.Services.CreateAsyncScope();

    public static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public async Task<Guid?> AddAsync(NotificationDraft draft, CancellationToken ct)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<INotificationWriter>().AddAsync(draft, ct);
    }

    public async Task<(Guid Id, string Email)> CreateUserAsync(bool emailConfirmed)
    {
        await using var scope = Scope();
        var id = Guid.CreateVersion7();
        var email = $"user-{id:N}@example.com";
        var user = new AppUser { Id = id, UserName = email, Email = email, FullName = "Nguyễn Văn Đạt", EmailConfirmed = emailConfirmed };
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().CreateAsync(user);
        result.Succeeded.ShouldBeTrue(string.Join(", ", result.Errors.Select(e => e.Description)));
        return (id, email);
    }

    /// <summary>The model every email test uses; it has every member the <c>order-paid</c> template references.</summary>
    public static object OrderModel(string orderCode = "TR-20261111-0042") => new
    {
        name = "Nguyễn Văn Đạt",
        eventTitle = "Đêm nhạc Mùa Thu",
        orderCode,
        amount = "1.250.000 ₫",
        startsAtText = "20:00, 15/11/2026",
        link = "http://localhost:3000/account/orders/1",
    };

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Outbox;

public sealed record TestEvent(int Number) : IDomainEvent;

public sealed record PoisonEvent : IDomainEvent;

public sealed record FlakyEvent(string Label) : IDomainEvent;

/// <summary>Shared, in-memory record of every handler invocation (including ones later rolled back).</summary>
public sealed class Invocations
{
    public ConcurrentQueue<Guid> MessageIds { get; } = new();

    public ConcurrentQueue<string> Labels { get; } = new();

    public ConcurrentDictionary<Guid, int> FailuresLeft { get; } = new();

    /// <summary>Set to make the next TestEvent handler simulate a process kill mid-handling.</summary>
    public CancellationTokenSource? CrashOnNext { get; set; }
}

/// <summary>Writes one row per handled message into <c>test_effects</c> inside the batch transaction.</summary>
public sealed class RecordingHandler(AppDbContext db, Invocations invocations) : IIntegrationEventHandler<TestEvent>
{
    public async Task HandleAsync(TestEvent message, IntegrationEventContext context, CancellationToken ct)
    {
        invocations.MessageIds.Enqueue(context.MessageId);
        await db.Database.ExecuteSqlAsync($"INSERT INTO test_effects (message_id) VALUES ({context.MessageId})", ct);

        if (invocations.CrashOnNext is { } crash)
        {
            invocations.CrashOnNext = null;
            await crash.CancelAsync();
            crash.Token.ThrowIfCancellationRequested();
        }
    }
}

public sealed class PoisonHandler : IIntegrationEventHandler<PoisonEvent>
{
    public Task HandleAsync(PoisonEvent message, IntegrationEventContext context, CancellationToken ct) =>
        throw new InvalidOperationException("poison");
}

public sealed class FlakyHandler(Invocations invocations) : IIntegrationEventHandler<FlakyEvent>
{
    public Task HandleAsync(FlakyEvent message, IntegrationEventContext context, CancellationToken ct)
    {
        if (invocations.FailuresLeft.TryGetValue(context.MessageId, out var left) && left > 0)
        {
            invocations.FailuresLeft[context.MessageId] = left - 1;
            throw new InvalidOperationException("transient");
        }

        invocations.Labels.Enqueue(message.Label);
        return Task.CompletedTask;
    }
}

/// <summary>A worker-like host: real infrastructure, test handlers, controllable clock.</summary>
public sealed class OutboxTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    public OutboxTestHost(string database, string redis, Invocations invocations, FakeTimeProvider clock)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(database, redis));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(invocations);
        builder.AddInfrastructure();
        builder.Services.AddApplication();
        builder.Services.AddIntegrationEventHandler<TestEvent, RecordingHandler>();
        builder.Services.AddIntegrationEventHandler<PoisonEvent, PoisonHandler>();
        builder.Services.AddIntegrationEventHandler<FlakyEvent, FlakyHandler>();
        _host = builder.Build();
    }

    public OutboxProcessor Processor => _host.Services.GetRequiredService<OutboxProcessor>();

    public static async Task CreateEffectsTableAsync(string database)
    {
        await using var db = ContainersFixture.CreateDbContext(database);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE test_effects (message_id uuid NOT NULL)");
    }

    public static OutboxMessage Message(IDomainEvent domainEvent, DateTimeOffset occurredAt, Guid? aggregateId = null) =>
        new(Guid.CreateVersion7(), DomainEventTypeRegistry.NameOf(domainEvent.GetType()), DomainEventTypeRegistry.Serialize(domainEvent), aggregateId, occurredAt);

    /// <summary>Runs batches until nothing is claimable.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        while (await Processor.ProcessBatchAsync(ct) > 0)
        {
        }
    }

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

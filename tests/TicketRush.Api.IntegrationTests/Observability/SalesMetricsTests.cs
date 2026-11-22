using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Tickets;
using TicketRush.Infrastructure.Observability;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Api.IntegrationTests.Observability;

/// <summary>Spec 17's meter as an exporter sees it, and the outbox measurement behind two of its gauges.</summary>
public sealed class SalesMetricsTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Meters : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose() => _meters.ForEach(m => m.Dispose());
    }

    /// <summary>Collects every measurement of the <c>TicketRush</c> meter, observable gauges included.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Listener()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SalesMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Seen.Add((i.Name, v, Tags(tags))));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Seen.Add((i.Name, v, Tags(tags))));
            _listener.Start();
        }

        public List<(string Name, double Value, string Tags)> Seen { get; } = [];

        public List<(string Name, double Value, string Tags)> Observe()
        {
            Seen.Clear();
            _listener.RecordObservableInstruments();
            return [.. Seen];
        }

        public void Dispose() => _listener.Dispose();

        private static string Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var parts = new List<string>();
            foreach (var tag in tags)
            {
                parts.Add($"{tag.Key}={tag.Value}");
            }

            return string.Join(',', parts);
        }
    }

    [Fact]
    public void Counters_carry_their_tags_and_the_gauges_report_only_what_was_observed()
    {
        using var meters = new Meters();
        using var listener = new Listener();
        var metrics = new SalesMetrics(meters);
        var phase = Guid.NewGuid();

        listener.Observe().ShouldBeEmpty();

        metrics.HoldCreated("ok");
        metrics.HoldCreated("hold.seats_unavailable");
        metrics.RefundRequested("event_cancelled");
        metrics.GatewayCall("create_session", TimeSpan.FromMilliseconds(120), succeeded: true);
        metrics.HoldsExpired(0);
        listener.Seen.ShouldBe(
            [
                ("ticketrush.holds.created_total", 1, "result=ok"),
                ("ticketrush.holds.created_total", 1, "result=hold.seats_unavailable"),
                ("ticketrush.orders.refund_total", 1, "reason=event_cancelled"),
                ("ticketrush.payment.gateway.duration", 0.12, "operation=create_session,outcome=ok"),
            ],
            ignoreOrder: false);

        metrics.QueueObserved(phase, 1200, 300);
        metrics.OutboxObserved(4.5, 2);
        var observed = listener.Observe();
        observed.ShouldContain(("ticketrush.queue.length", 1200, $"phase={phase}"));
        observed.ShouldContain(("ticketrush.queue.active_shoppers", 300, $"phase={phase}"));
        observed.ShouldContain(("ticketrush.outbox.lag_seconds", 4.5, ""));
        observed.ShouldContain(("ticketrush.outbox.dead_letters", 2, ""));

        metrics.QueueClosed(phase);
        listener.Observe().ShouldNotContain(m => m.Name.StartsWith("ticketrush.queue.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_outbox_lag_is_the_age_of_the_oldest_due_message_and_dead_letters_are_counted_until_discarded()
    {
        await using var world = await TicketTestWorld.CreateAsync(containers);
        await world.Factory.DrainOutboxAsync(Ct);
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var stats = scope.ServiceProvider.GetRequiredService<OutboxStats>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        (await stats.ReadAsync(Ct)).ShouldBe((0d, 0L));

        var due = Guid.CreateVersion7();
        var later = Guid.CreateVersion7();
        await world.WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage(due, "Probe", "{}", null, now.AddSeconds(-40)));
            db.OutboxMessages.Add(new OutboxMessage(later, "Probe", "{}", null, now.AddMinutes(5)));
            return await db.SaveChangesAsync(Ct);
        });
        (await stats.ReadAsync(Ct)).LagSeconds.ShouldBe(40, tolerance: 1);

        await world.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE outbox_messages SET processed_at = {now}, last_error = 'boom' WHERE id = {due}", Ct));
        var (lag, dead) = await stats.ReadAsync(Ct);
        lag.ShouldBe(0);
        dead.ShouldBe(1);

        await world.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE outbox_messages SET discarded_at = {now} WHERE id = {due}", Ct));
        (await stats.ReadAsync(Ct)).DeadLetters.ShouldBe(0);
        await world.WithDbAsync(db => db.Database.ExecuteSqlAsync($"DELETE FROM outbox_messages WHERE id IN ({due}, {later})", Ct));
    }
}

using System.Diagnostics;
using TicketRush.Infrastructure.Observability;

namespace TicketRush.Api.IntegrationTests.Observability;

public sealed class TraceContextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-01")]
    public void A_missing_or_broken_stored_parent_starts_a_new_trace_instead_of_failing(string? stored) =>
        TraceContext.ParentOf(stored).ShouldBe(default);

    [Fact]
    public void A_stored_parent_is_continued_and_the_current_span_is_what_gets_stored()
    {
        using var source = new ActivitySource("TicketRush.Tests.Trace");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        TraceContext.CurrentParent().ShouldBeNull();
        using var span = source.StartActivity("work")!;
        var stored = TraceContext.CurrentParent();
        stored.ShouldBe(span.Id);

        var parent = TraceContext.ParentOf(stored);
        parent.TraceId.ShouldBe(span.TraceId);
        parent.SpanId.ShouldBe(span.SpanId);
    }
}

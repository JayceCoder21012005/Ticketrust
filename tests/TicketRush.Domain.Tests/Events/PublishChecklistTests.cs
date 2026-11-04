using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Events.Publishing;
using TicketRush.Domain.Events.Publishing.Rules;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Domain.Tests.Events;

public sealed class PublishChecklistTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = Now.AddDays(30);

    /// <summary>An event that passes every rule: cover, one seated tier with seats, one GA tier, one phase in 1 day.</summary>
    private static (Event Event, Dictionary<Guid, int> Seats) Ready()
    {
        var venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);
        var evt = Event.CreateDraft(Guid.CreateVersion7(), "slug", "Title", "concert", venue, Start, Start.AddHours(3));
        evt.UpdateInfo("Title", "", [], "https://cdn/events/cover.jpg");
        evt.ReplaceTiers(
        [
            new TierDraft(null, "Ngồi", new Money(500_000), TicketKind.Seated, null, "#7c3aed"),
            new TierDraft(null, "Đứng", new Money(300_000), TicketKind.GeneralAdmission, 500, "#0f766e"),
        ]);
        evt.ReplaceLayout(new SeatLayoutDefinition(
            [new SectionDefinition("A", evt.Tiers[0].Id, SectionKind.Seated, 2, 3, new LayoutPoint(0, 0))]));
        evt.ReplacePhases([Phase(Now.AddDays(1), Now.AddDays(2))]);
        return (evt, new Dictionary<Guid, int> { [evt.Tiers[0].Id] = 6 });
    }

    private static PhaseDraft Phase(DateTimeOffset opens, DateTimeOffset closes) =>
        new(null, "Mở bán", opens, closes, true, 4, null, false);

    private static IReadOnlyList<PublishFailure> Run(Event evt, IReadOnlyDictionary<Guid, int> seats, DateTimeOffset? now = null) =>
        PublishChecklist.Run(new PublishContext(evt, seats, now ?? Now));

    [Fact]
    public void A_complete_draft_passes_every_rule()
    {
        var (evt, seats) = Ready();

        Run(evt, seats).ShouldBeEmpty();
    }

    [Fact]
    public void Missing_cover_fails_on_the_info_step()
    {
        var (evt, seats) = Ready();
        evt.UpdateInfo("Title", "", [], null);

        Run(evt, seats).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Code.ShouldBe("publish.missing_cover"),
            f => f.Step.ShouldBe("info"));
    }

    [Fact]
    public void No_tiers_fails_on_the_tiers_step()
    {
        var (evt, _) = Ready();
        var noLayout = Event.CreateDraft(evt.OrganizerId, "s2", "Title", "concert", Venue.Create("V", "A", "Huế", null, 10), Start, Start.AddHours(1));
        noLayout.UpdateInfo("Title", "", [], "https://cdn/c.jpg");
        noLayout.ReplacePhases([Phase(Now.AddDays(1), Now.AddDays(2))]);

        Run(noLayout, new Dictionary<Guid, int>()).Select(f => (f.Code, f.Step)).ShouldBe([("publish.no_tiers", "tiers")]);
    }

    [Fact]
    public void A_seated_tier_without_seats_fails_on_the_layout_step()
    {
        var (evt, _) = Ready();

        Run(evt, new Dictionary<Guid, int>()).Select(f => (f.Code, f.Step)).ShouldBe([("publish.tier_without_seats", "layout")]);
    }

    [Fact]
    public void No_phases_fails_on_the_phases_step()
    {
        var (evt, seats) = Ready();
        evt.ReplacePhases([]);

        Run(evt, seats).Select(f => (f.Code, f.Step)).ShouldBe([("publish.no_phases", "phases")]);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void First_phase_must_open_at_least_10_minutes_ahead(int minutes, bool fails)
    {
        var (evt, seats) = Ready();
        evt.ReplacePhases([Phase(Now.AddMinutes(minutes), Now.AddDays(2))]);

        var codes = Run(evt, seats).Select(f => f.Code);

        if (fails)
        {
            codes.ShouldBe(["publish.first_phase_too_soon"]);
        }
        else
        {
            codes.ShouldBeEmpty();
        }
    }

    [Fact]
    public void GaTiersHaveCapacityRule_flags_a_GA_tier_without_capacity()
    {
        // The tier constructor already refuses this, so the rule guards rows written outside the aggregate.
        GaTiersHaveCapacityRule.Lacking([(TicketKind.GeneralAdmission, 0), (TicketKind.Seated, null), (TicketKind.GeneralAdmission, 10)])
            .ShouldBeTrue();
        GaTiersHaveCapacityRule.Lacking([(TicketKind.GeneralAdmission, 10), (TicketKind.Seated, null)]).ShouldBeFalse();
    }

    [Fact]
    public void PhasesDoNotOverlapRule_detects_overlapping_windows()
    {
        var t = Now;

        PhasesDoNotOverlapRule.AnyOverlap([(t, t.AddHours(2)), (t.AddHours(1), t.AddHours(3))]).ShouldBeTrue();
        PhasesDoNotOverlapRule.AnyOverlap([(t, t.AddHours(1)), (t.AddHours(1), t.AddHours(2))]).ShouldBeFalse();
        PhasesDoNotOverlapRule.AnyOverlap([(t.AddHours(5), t.AddHours(6)), (t, t.AddHours(1)), (t.AddMinutes(30), t.AddHours(2))])
            .ShouldBeTrue();
    }

    [Fact]
    public void PhasesCloseBeforeStartRule_detects_a_phase_closing_after_the_event_starts()
    {
        // UpdateSchedule refuses this too, so the rule guards rows written outside the aggregate (and older drafts).
        PhasesCloseBeforeStartRule.AnyClosesAfter(Start, [Start.AddDays(-1), Start.AddMinutes(1)]).ShouldBeTrue();
        PhasesCloseBeforeStartRule.AnyClosesAfter(Start, [Start.AddDays(-1), Start]).ShouldBeFalse();
        new PhasesCloseBeforeStartRule().Check(new PublishContext(Ready().Event, new Dictionary<Guid, int>(), Now)).ShouldBeNull();
    }

    [Fact]
    public void Run_reports_every_failure_in_rule_order()
    {
        var venue = Venue.Create("V", "A", "Huế", null, 10);
        var empty = Event.CreateDraft(Guid.CreateVersion7(), "s", "Title", "concert", venue, Start, Start.AddHours(1));

        Run(empty, new Dictionary<Guid, int>()).Select(f => f.Code)
            .ShouldBe(["publish.missing_cover", "publish.no_tiers", "publish.no_phases"]);
    }

    [Fact]
    public void Every_rule_is_listed_once_in_the_documented_order()
    {
        PublishChecklist.Rules.Select(r => r.GetType().Name).ShouldBe(
        [
            nameof(HasCoverRule),
            nameof(HasTierRule),
            nameof(SeatedTiersHaveSeatsRule),
            nameof(GaTiersHaveCapacityRule),
            nameof(HasPhaseRule),
            nameof(FirstPhaseOpensLaterRule),
            nameof(PhasesDoNotOverlapRule),
            nameof(PhasesCloseBeforeStartRule),
        ]);
    }
}

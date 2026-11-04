using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Domain.Tests.Events;

public sealed class EventEditingTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 6, 12, 30, 0, TimeSpan.Zero);

    private static readonly Venue Venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);

    private static Event NewDraft() =>
        Event.CreateDraft(Guid.CreateVersion7(), "slug", "Title", "concert", Venue, Start, Start.AddHours(3));

    private static TierDraft Tier(string name, Guid? id = null, TicketKind kind = TicketKind.Seated, int? capacity = null) =>
        new(id, name, new Money(500_000), kind, capacity, "#7c3aed");

    private static PhaseDraft Phase(DateTimeOffset opens, DateTimeOffset closes, Guid? id = null, string? hash = null, bool keep = false) =>
        new(id, "Mở bán", opens, closes, QueueEnabled: true, MaxTicketsPerUser: 4, hash, keep);

    private static Event Published()
    {
        var evt = NewDraft();
        evt.AddTier("VIP", new Money(1_000_000), TicketKind.GeneralAdmission, 100, "#7c3aed");
        evt.AddPhase("Mở bán", Start.AddDays(-10), Start.AddDays(-1), queueEnabled: true, maxTicketsPerUser: 4);
        evt.UpdateInfo("Title", "", [], "https://cdn/events/x.jpg");
        evt.Publish();
        evt.DequeueDomainEvents();
        return evt;
    }

    [Fact]
    public void ReplaceTiers_upserts_by_id_and_removes_missing()
    {
        var evt = NewDraft();
        evt.ReplaceTiers([Tier("Thường"), Tier("VIP")]);
        var first = evt.Tiers[0].Id;
        var second = evt.Tiers[1].Id;

        evt.ReplaceTiers([Tier("Phổ thông", first), Tier("SVIP")]);

        evt.Tiers.Count.ShouldBe(2);
        evt.Tiers.ShouldContain(t => t.Id == first && t.Name == "Phổ thông");
        evt.Tiers.ShouldNotContain(t => t.Id == second);
        evt.Tiers.ShouldContain(t => t.Name == "SVIP" && t.Id != first && t.Id != second);
    }

    [Fact]
    public void ReplaceTiers_with_an_unknown_id_throws_tier_not_found()
    {
        Should.Throw<DomainException>(() => NewDraft().ReplaceTiers([Tier("X", Guid.NewGuid())]))
            .Code.ShouldBe("tier.not_found");
    }

    [Fact]
    public void ReplaceTiers_after_publish_throws_not_editable()
    {
        Should.Throw<DomainException>(() => Published().ReplaceTiers([Tier("X")]))
            .Code.ShouldBe("event.not_editable");
    }

    [Theory]
    [InlineData(TicketKind.Seated, SectionKind.Seated)]
    [InlineData(TicketKind.GeneralAdmission, SectionKind.Standing)]
    public void ReplaceTiers_changing_the_kind_of_a_tier_the_layout_uses_throws_tier_in_use(TicketKind kind, SectionKind section)
    {
        var evt = NewDraft();
        evt.ReplaceTiers([Tier("VIP", kind: kind, capacity: kind == TicketKind.GeneralAdmission ? 100 : null), Tier("Khác")]);
        var used = evt.Tiers[0].Id;
        evt.ReplaceLayout(new SeatLayoutDefinition(
        [
            new SectionDefinition("A", used, section, 2, 3, new LayoutPoint(0, 0)),
            new SectionDefinition("B", used, section, 2, 3, new LayoutPoint(200, 0)),
        ]));
        var other = kind == TicketKind.Seated ? TicketKind.GeneralAdmission : TicketKind.Seated;

        var error = Should.Throw<DomainException>(() => evt.ReplaceTiers(
            [Tier("VIP", used, other, other == TicketKind.GeneralAdmission ? 500 : null), Tier("Khác", evt.Tiers[1].Id)]));

        error.Code.ShouldBe("tier.in_use");
        error.Message.ShouldContain("A, B");
        evt.Tiers[0].Kind.ShouldBe(kind);
    }

    [Fact]
    public void ReplaceTiers_may_change_the_kind_of_a_tier_no_section_uses()
    {
        var evt = NewDraft();
        evt.ReplaceTiers([Tier("VIP"), Tier("Khác")]);
        evt.ReplaceLayout(new SeatLayoutDefinition([new SectionDefinition("A", evt.Tiers[0].Id, SectionKind.Seated, 1, 1, new LayoutPoint(0, 0))]));

        evt.ReplaceTiers([Tier("VIP", evt.Tiers[0].Id), Tier("Khác", evt.Tiers[1].Id, TicketKind.GeneralAdmission, 300)]);

        evt.Tiers[1].Kind.ShouldBe(TicketKind.GeneralAdmission);
    }

    [Fact]
    public void ReplacePhases_overlapping_throws_phase_overlap()
    {
        var day = Start.AddDays(-5);

        Should.Throw<DomainException>(() => NewDraft().ReplacePhases(
                [Phase(day.AddHours(10), day.AddHours(12)), Phase(day.AddHours(11), day.AddHours(13))]))
            .Code.ShouldBe("event.phase_overlap");
    }

    [Fact]
    public void ReplacePhases_touching_windows_are_allowed()
    {
        var day = Start.AddDays(-5);
        var evt = NewDraft();

        evt.ReplacePhases([Phase(day.AddHours(10), day.AddHours(11)), Phase(day.AddHours(11), day.AddHours(12))]);

        evt.Phases.Count.ShouldBe(2);
    }

    [Fact]
    public void ReplacePhases_closing_after_the_event_starts_throws()
    {
        Should.Throw<DomainException>(() => NewDraft().ReplacePhases([Phase(Start.AddDays(-1), Start.AddMinutes(1))]))
            .Code.ShouldBe("phase.after_event_start");
    }

    [Fact]
    public void ReplacePhases_keeps_hash_for_null_code_and_clears_for_empty()
    {
        var day = Start.AddDays(-5);
        var evt = NewDraft();
        evt.ReplacePhases([Phase(day, day.AddHours(1), hash: "h1"), Phase(day.AddHours(2), day.AddHours(3), hash: "h2")]);
        var kept = evt.Phases[0].Id;
        var cleared = evt.Phases[1].Id;

        evt.ReplacePhases(
        [
            Phase(day, day.AddHours(1), id: kept, keep: true),
            Phase(day.AddHours(2), day.AddHours(3), id: cleared),
            Phase(day.AddHours(4), day.AddHours(5), keep: true),
        ]);

        evt.Phases.Single(p => p.Id == kept).AccessCodeHash.ShouldBe("h1");
        evt.Phases.Single(p => p.Id == cleared).AccessCodeHash.ShouldBeNull();
        evt.Phases.Single(p => p.Id != kept && p.Id != cleared).AccessCodeHash.ShouldBeNull();
    }

    [Fact]
    public void ReplacePhases_after_publish_throws_not_editable()
    {
        Should.Throw<DomainException>(() => Published().ReplacePhases([]))
            .Code.ShouldBe("event.not_editable");
    }

    [Fact]
    public void UpdateInfo_on_published_raises_EventDetailsChanged_once_and_not_when_unchanged()
    {
        var evt = Published();

        evt.UpdateInfo("Title", "", [], "https://cdn/events/x.jpg");
        evt.DomainEvents.ShouldBeEmpty();

        evt.UpdateInfo("Tên mới", "Mô tả", ["Ca sĩ"], "https://cdn/events/x.jpg");
        evt.DomainEvents.ShouldHaveSingleItem().ShouldBe(new EventDetailsChanged(evt.Id));
        evt.Title.ShouldBe("Tên mới");
    }

    [Fact]
    public void UpdateInfo_on_draft_raises_nothing()
    {
        var evt = NewDraft();

        evt.UpdateInfo("Tên mới", "Mô tả", [], null);

        evt.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("   ")]
    public void UpdateInfo_rejects_a_too_short_title(string title)
    {
        Should.Throw<DomainException>(() => NewDraft().UpdateInfo(title, "", [], null))
            .Code.ShouldBe("event.invalid_title");
    }

    [Fact]
    public void UpdateSchedule_moves_venue_city_and_dates_on_a_draft()
    {
        var evt = NewDraft();
        var other = Venue.Create("Nhà hát Hoà Bình", "240 Ba Tháng Hai", "TP. Hồ Chí Minh", null, 2000);

        evt.UpdateSchedule("theater", other, Start.AddDays(1), Start.AddDays(1).AddHours(2));

        evt.VenueId.ShouldBe(other.Id);
        evt.City.ShouldBe("TP. Hồ Chí Minh");
        evt.Category.ShouldBe("theater");
        evt.StartsAt.ShouldBe(Start.AddDays(1));
    }

    [Fact]
    public void UpdateSchedule_moving_the_start_before_a_phase_closes_throws_phase_after_event_start()
    {
        var evt = NewDraft();
        evt.ReplacePhases([Phase(Start.AddDays(-10), Start.AddDays(-2))]);

        Should.Throw<DomainException>(() => evt.UpdateSchedule("concert", Venue, Start.AddDays(-5), Start.AddDays(-5).AddHours(2)))
            .Code.ShouldBe("phase.after_event_start");
        evt.StartsAt.ShouldBe(Start);
    }

    [Fact]
    public void UpdateSchedule_may_move_the_start_to_the_moment_the_last_phase_closes()
    {
        var evt = NewDraft();
        evt.ReplacePhases([Phase(Start.AddDays(-10), Start.AddDays(-2))]);

        evt.UpdateSchedule("concert", Venue, Start.AddDays(-2), Start.AddDays(-2).AddHours(2));

        evt.StartsAt.ShouldBe(Start.AddDays(-2));
    }

    [Fact]
    public void UpdateSchedule_after_publish_throws_not_editable()
    {
        Should.Throw<DomainException>(() => Published().UpdateSchedule("concert", Venue, Start, Start.AddHours(1)))
            .Code.ShouldBe("event.not_editable");
    }

    [Fact]
    public void Times_entered_in_Vietnam_time_are_stored_as_the_same_instant_in_UTC()
    {
        var vn = TimeSpan.FromHours(7);
        var opens = new DateTimeOffset(2026, 12, 1, 0, 30, 0, vn);
        var start = new DateTimeOffset(2026, 12, 6, 19, 30, 0, vn);

        var evt = Event.CreateDraft(Guid.CreateVersion7(), "s", "Title", "concert", Venue, start, start.AddHours(3));
        evt.ReplacePhases([Phase(opens, opens.AddDays(1))]);
        evt.UpdateSchedule("concert", Venue, start.AddDays(1), start.AddDays(1).AddHours(2));

        evt.StartsAt.Offset.ShouldBe(TimeSpan.Zero);
        evt.StartsAt.ShouldBe(start.AddDays(1));
        evt.EndsAt.Offset.ShouldBe(TimeSpan.Zero);
        evt.Phases[0].OpensAt.Offset.ShouldBe(TimeSpan.Zero);
        evt.Phases[0].OpensAt.ShouldBe(opens);
        evt.Phases[0].ClosesAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Venue_created_by_organizer_has_owner()
    {
        var owner = Guid.CreateVersion7();

        Venue.Create("Sân khấu riêng", "1 Lê Lợi", "Huế", null, 300, organizerId: owner).OrganizerId.ShouldBe(owner);
        Venue.OrganizerId.ShouldBeNull();
    }
}

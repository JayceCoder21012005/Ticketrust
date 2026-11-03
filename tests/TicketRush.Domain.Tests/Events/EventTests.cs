using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Domain.Tests.Events;

public sealed class EventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    private static readonly Venue Venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);

    private static Event NewDraft() =>
        Event.CreateDraft(Guid.CreateVersion7(), "slug", "Title", "concert", Venue, Now.AddDays(30), Now.AddDays(30).AddHours(4));

    [Fact]
    public void CreateDraft_copies_the_venue_and_its_city_so_search_and_city_filters_stay_on_events()
    {
        var evt = NewDraft();

        evt.VenueId.ShouldBe(Venue.Id);
        evt.City.ShouldBe("Hà Nội");
    }

    [Fact]
    public void CreateDraft_rejects_an_unknown_category()
    {
        Should.Throw<DomainException>(() =>
                Event.CreateDraft(Guid.NewGuid(), "s", "t", "karaoke", Venue, Now, Now.AddHours(1)))
            .Code.ShouldBe("event.invalid_category");
    }

    [Fact]
    public void Categories_have_unique_slugs_and_vietnamese_names()
    {
        EventCategories.All.Select(c => c.Slug).ShouldBeUnique();
        EventCategories.All.ShouldAllBe(c => c.Name.Length > 0);
        EventCategories.IsKnown("concert").ShouldBeTrue();
        EventCategories.IsKnown("CONCERT").ShouldBeFalse();
    }

    [Fact]
    public void CreateDraft_rejects_an_event_that_ends_before_it_starts()
    {
        var ex = Should.Throw<DomainException>(() =>
            Event.CreateDraft(Guid.NewGuid(), "s", "t", "concert", Venue, Now, Now.AddMinutes(-1)));

        ex.Code.ShouldBe("event.invalid_schedule");
    }

    [Fact]
    public void Publish_moves_draft_to_published_and_raises_EventPublished_once()
    {
        var evt = NewDraft();

        evt.Publish();
        evt.Publish();

        evt.Status.ShouldBe(EventStatus.Published);
        evt.DomainEvents.ShouldHaveSingleItem().ShouldBe(new EventPublished(evt.Id, evt.OrganizerId));
    }

    [Fact]
    public void Cancel_is_allowed_from_draft_and_published_but_not_from_ended()
    {
        var draft = NewDraft();
        draft.Cancel();
        draft.Status.ShouldBe(EventStatus.Cancelled);

        var ended = NewDraft();
        ended.Publish();
        ended.MarkEnded();
        Should.Throw<DomainException>(ended.Cancel).Code.ShouldBe("event.not_cancellable");
    }

    [Fact]
    public void MarkEnded_requires_a_published_event()
    {
        Should.Throw<DomainException>(NewDraft().MarkEnded).Code.ShouldBe("event.not_endable");
    }

    [Fact]
    public void Cancelled_event_cannot_be_published()
    {
        var evt = NewDraft();
        evt.Cancel();

        Should.Throw<DomainException>(evt.Publish).Code.ShouldBe("event.not_publishable");
    }

    [Fact]
    public void AddPhase_rejects_overlapping_phases()
    {
        var evt = NewDraft();
        evt.AddPhase("Presale", Now.AddDays(1), Now.AddDays(3), queueEnabled: true, maxTicketsPerUser: 2);

        Should.Throw<DomainException>(() => evt.AddPhase("Main", Now.AddDays(2), Now.AddDays(5), true, 4))
            .Code.ShouldBe("event.phase_overlap");
    }

    [Fact]
    public void AddPhase_accepts_back_to_back_phases()
    {
        var evt = NewDraft();
        evt.AddPhase("Presale", Now.AddDays(1), Now.AddDays(3), true, 2);

        evt.AddPhase("Main", Now.AddDays(3), Now.AddDays(5), true, 4);

        evt.Phases.Count.ShouldBe(2);
    }

    [Fact]
    public void AddPhase_rejects_a_phase_closing_after_the_event_starts()
    {
        var evt = NewDraft();

        Should.Throw<DomainException>(() => evt.AddPhase("Late", Now.AddDays(1), Now.AddDays(31), true, 4))
            .Code.ShouldBe("phase.after_event_start");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void AddPhase_limits_tickets_per_user_to_1_through_8(int max)
    {
        Should.Throw<DomainException>(() => NewDraft().AddPhase("P", Now.AddDays(1), Now.AddDays(2), true, max))
            .Code.ShouldBe("phase.invalid_limit");
    }

    [Fact]
    public void Tiers_are_frozen_after_publish()
    {
        var evt = NewDraft();
        evt.Publish();

        Should.Throw<DomainException>(() => evt.AddTier("VIP", new Money(1000), TicketKind.Seated, null, "#000"))
            .Code.ShouldBe("event.not_editable");
    }

    [Fact]
    public void AddTier_rejects_zero_price_and_general_admission_without_capacity()
    {
        var evt = NewDraft();

        Should.Throw<DomainException>(() => evt.AddTier("Free", Money.Zero(), TicketKind.Seated, null, "#000"))
            .Code.ShouldBe("tier.invalid_price");
        Should.Throw<DomainException>(() => evt.AddTier("GA", new Money(1000), TicketKind.GeneralAdmission, null, "#000"))
            .Code.ShouldBe("tier.invalid_capacity");
    }

    [Fact]
    public void DequeueDomainEvents_returns_and_clears_pending_events()
    {
        var evt = NewDraft();
        evt.Publish();

        evt.DequeueDomainEvents().Count.ShouldBe(1);
        evt.DomainEvents.ShouldBeEmpty();
    }
}

using TicketRush.Application.Features.Catalog;

namespace TicketRush.Application.Tests.Catalog;

public sealed class HotScoreTests
{
    [Fact]
    public void Recent_sales_weigh_three_a_ticket_sold_ratio_one_a_percent_followers_half()
    {
        HotScore.Of(recentSold: 5, sold: 50, capacity: 100, followers: 4).ShouldBe((5 * 3) + 50 + 2);
    }

    [Fact]
    public void An_event_without_capacity_scores_on_sales_and_followers_only()
    {
        HotScore.Of(recentSold: 2, sold: 0, capacity: 0, followers: 2).ShouldBe(7);
    }

    [Fact]
    public void An_event_appears_in_one_section_only_and_a_sale_about_to_open_keeps_its_countdown()
    {
        var now = new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        HomeCandidate Candidate(string slug, int startsInDays, bool onSale, int? opensInDays, int followers) => new(
            new EventSummary(Guid.NewGuid(), slug, slug, "concert", now.AddDays(startsInDays), "V", "Hà Nội", null, 100,
                opensInDays is { } d ? now.AddDays(d) : null, onSale, false),
            0, 0, 100, followers);
        var candidates = new[]
        {
            Candidate("hot-this-week", 3, onSale: true, null, followers: 50),
            Candidate("opening-in-2-days", 30, onSale: false, 2, followers: 40),
            Candidate("plain-this-week", 4, onSale: false, null, followers: 0),
            Candidate("later", 60, onSale: false, null, followers: 0),
        };

        var home = HomeSectionPicker.Pick(candidates, now);

        var all = home.Hot.Concat(home.OpeningSoon).Concat(home.ThisWeek).Concat(home.Upcoming).Select(e => e.Slug).ToList();
        all.ShouldBeUnique();
        home.OpeningSoon.Select(e => e.Slug).ShouldBe(["opening-in-2-days"]);
        home.Hot.Select(e => e.Slug).ShouldBe(["hot-this-week"]);
        home.ThisWeek.Select(e => e.Slug).ShouldBe(["plain-this-week"]);
        home.Upcoming.Select(e => e.Slug).ShouldBe(["later"]);
    }

    [Fact]
    public void Recent_sales_beat_a_higher_sold_ratio()
    {
        HotScore.Of(recentSold: 40, sold: 40, capacity: 1000, followers: 0)
            .ShouldBeGreaterThan(HotScore.Of(recentSold: 0, sold: 90, capacity: 100, followers: 0));
    }
}

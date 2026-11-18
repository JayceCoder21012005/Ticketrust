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
    public void Recent_sales_beat_a_higher_sold_ratio()
    {
        HotScore.Of(recentSold: 40, sold: 40, capacity: 1000, followers: 0)
            .ShouldBeGreaterThan(HotScore.Of(recentSold: 0, sold: 90, capacity: 100, followers: 0));
    }
}

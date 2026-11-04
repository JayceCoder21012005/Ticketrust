using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Holds;

public sealed class InventoryKeysTests
{
    private static readonly Guid Event = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("99999999-2222-3333-4444-555555555555");
    private static readonly Guid Tier = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid User = Guid.Parse("bbbbbbbb-2222-3333-4444-555555555555");
    private static readonly Guid Hold = Guid.Parse("cccccccc-2222-3333-4444-555555555555");

    private static RedisKeys Keys() => new(Options.Create(new RedisOptions { Environment = "x" }));

    private static string[] AllOf(RedisKeys keys, Guid eventId) => new[]
    {
        keys.InvSold(eventId), keys.InvHeld(eventId), keys.InvGa(eventId, Tier), keys.InvGaHeld(eventId),
        keys.InvHold(eventId, Hold), keys.InvExpiry(eventId), keys.InvUserHold(eventId, User), keys.InvBought(eventId, User),
        keys.InvVersion(eventId), keys.InvMeta(eventId),
    }.Select(k => k.ToString()).ToArray();

    [Fact]
    public void Held_key_has_prefix_hash_tag_and_name()
    {
        Keys().InvHeld(Event).ToString().ShouldBe("tr:x:inv:{evt:11111111222233334444555555555555}:held");
    }

    [Fact]
    public void Every_key_of_one_event_shares_one_hash_tag()
    {
        var all = AllOf(Keys(), Event);

        all.Distinct().Count().ShouldBe(10);
        all.ShouldAllBe(k => k.Contains("{evt:11111111222233334444555555555555}"));
    }

    [Fact]
    public void Keys_of_two_events_do_not_collide()
    {
        var keys = Keys();

        AllOf(keys, Event).Intersect(AllOf(keys, Other)).ShouldBeEmpty();
    }

    [Fact]
    public void Change_channel_matches_the_subscription_pattern()
    {
        var keys = Keys();

        keys.InvChanges(Event).ToString().ShouldBe("tr:x:inv:{evt:11111111222233334444555555555555}:changes");
        keys.InvChangesPattern.ToString().ShouldBe("tr:x:inv:*:changes");
    }
}

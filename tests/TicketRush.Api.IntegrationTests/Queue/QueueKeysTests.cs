using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class QueueKeysTests
{
    private static readonly Guid Phase = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static RedisKeys Keys() => new(Options.Create(new RedisOptions { Environment = "x" }));

    [Fact]
    public void Line_key_has_prefix_hash_tag_and_name()
    {
        Keys().QueueLine(Phase).ToString().ShouldBe("tr:x:q:{sale:11111111222233334444555555555555}:line");
    }

    [Fact]
    public void All_queue_keys_share_one_hash_tag()
    {
        var keys = Keys();
        var all = new[]
        {
            keys.QueueState(Phase), keys.QueuePre(Phase), keys.QueueLine(Phase), keys.QueueSeq(Phase),
            keys.QueueAdmitted(Phase), keys.QueueActive(Phase), keys.QueueTokens(Phase), keys.QueueDone(Phase),
        }.Select(k => k.ToString()).ToArray();

        all.Distinct().Count().ShouldBe(8);
        all.ShouldAllBe(k => k.Contains("{sale:11111111222233334444555555555555}"));
    }
}

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Application.Features.Queue;
using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Queue;

public sealed class QueueHandlerTests
{
    private const string GoodCode = "VIP-2026";
    private const string GoodHash = "hash-of-vip-2026";

    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly IWaitingRoom _room = Substitute.For<IWaitingRoom>();
    private readonly ISaleWindowProvider _sales = Substitute.For<ISaleWindowProvider>();
    private readonly IAccessCodeHasher _hasher = Substitute.For<IAccessCodeHasher>();

    public QueueHandlerTests()
    {
        _hasher.Hash(Arg.Any<string>()).Returns(call => call.Arg<string>() == GoodCode ? GoodHash : "wrong");
        _room.JoinAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new JoinResult(RoomStatus.Open, 7));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Open, 7, 0, 7, 0, false, false, false, false, null, null));
    }

    private SaleWindow Phase(TimeSpan opensIn, bool queue = true, string? hash = null)
    {
        var window = new SaleWindow(EventId, Guid.NewGuid(), _time.GetUtcNow() + opensIn, _time.GetUtcNow() + opensIn + TimeSpan.FromHours(2), queue);
        _sales.GetAsync(EventId, Arg.Any<CancellationToken>()).Returns(new EventSales(EventId, [new SalePhaseInfo(window, hash)]));
        return window;
    }

    private JoinQueueHandler JoinHandler() => new(_sales, _room, _hasher, Microsoft.Extensions.Options.Options.Create(new QueueOptions()), _time);

    private GetQueueStatusHandler StatusHandler() => new(_sales, _room, Microsoft.Extensions.Options.Options.Create(new QueueOptions()), _time);

    [Fact]
    public async Task Unconfirmed_email_is_rejected()
    {
        Phase(TimeSpan.FromMinutes(-1));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, EmailVerified: false, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.email_not_confirmed");
        await _room.DidNotReceiveWithAnyArgs().JoinAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unknown_event_is_not_found()
    {
        _sales.GetAsync(EventId, Arg.Any<CancellationToken>()).Returns((EventSales?)null);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("event.not_found");
    }

    [Fact]
    public async Task Join_before_the_window_is_not_open()
    {
        Phase(TimeSpan.FromMinutes(31)); // queued sales open their room 30 min early

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_open");
    }

    [Fact]
    public async Task Queued_sale_accepts_joins_from_thirty_minutes_before_opening()
    {
        Phase(TimeSpan.FromMinutes(29));
        _room.JoinAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>()).Returns(new JoinResult(RoomStatus.Pre, null));
        _room.StatusAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Pre, null, 0, 0, 0, false, false, false, true, null, null));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(QueueClientStatus.Pre);
    }

    [Fact]
    public async Task Non_queued_sale_does_not_open_early()
    {
        Phase(TimeSpan.FromMinutes(5), queue: false);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_open");
    }

    [Fact]
    public async Task Join_after_the_sale_closed_says_closed()
    {
        Phase(TimeSpan.FromHours(-3));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.closed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Wrong_or_missing_access_code_is_rejected(string? code)
    {
        Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, code), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.invalid_access_code");
        await _room.DidNotReceiveWithAnyArgs().JoinAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Right_access_code_joins()
    {
        Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, GoodCode), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_reloading_the_page_rejoins_without_retyping_the_presale_code()
    {
        var phase = Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);
        _room.IsMemberAsync(phase, User, Arg.Any<CancellationToken>()).Returns(true);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_failure_maps_to_system_busy()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.JoinAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new QueueUnavailableException("down"));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("system.busy");
    }

    [Fact]
    public async Task Status_before_joining_is_not_joined()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Open, null, 0, 0, 0, false, false, false, false, null, null));

        var result = await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_joined");
    }

    [Fact]
    public async Task Status_redis_failure_maps_to_system_busy()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new QueueUnavailableException("down"));

        (await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("system.busy");
    }

    [Fact]
    public async Task Leave_removes_the_user_from_the_selected_phase()
    {
        var phase = Phase(TimeSpan.FromMinutes(-1));
        var handler = new LeaveQueueHandler(_sales, _room, _time);

        var result = await handler.HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).LeaveAsync(phase, User, Arg.Any<CancellationToken>());
    }
}

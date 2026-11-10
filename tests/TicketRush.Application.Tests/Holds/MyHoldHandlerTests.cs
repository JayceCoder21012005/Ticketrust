using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Holds;

namespace TicketRush.Application.Tests.Holds;

public sealed class MyHoldHandlerTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly IInventoryStore _store = Substitute.For<IInventoryStore>();
    private readonly IHoldCatalog _catalog = Substitute.For<IHoldCatalog>();

    public MyHoldHandlerTests() =>
        _catalog.GetAsync(HoldTestCatalog.EventId, Arg.Any<CancellationToken>()).Returns(HoldTestCatalog.Build(_time.GetUtcNow()));

    private ActiveHold Hold() => new(Guid.NewGuid(), User, [4], [new GaLine(HoldTestCatalog.Ga, 2)], _time.GetUtcNow().AddMinutes(5));

    [Fact]
    public async Task Get_returns_the_priced_hold()
    {
        var hold = Hold();
        _store.GetUserHoldAsync(HoldTestCatalog.EventId, User, Arg.Any<CancellationToken>()).Returns(hold);

        var result = await new GetMyHoldHandler(_catalog, _store, _time).HandleAsync(User, HoldTestCatalog.EventId, Ct);

        result.Value.HoldId.ShouldBe(hold.HoldId);
        result.Value.Total.ShouldBe(1_500_000 + 2 * 500_000);
        result.Value.ExpiresAt.ShouldBe(hold.ExpiresAt);
    }

    [Fact]
    public async Task Get_without_a_hold_is_not_found()
    {
        var result = await new GetMyHoldHandler(_catalog, _store, _time).HandleAsync(User, HoldTestCatalog.EventId, Ct);

        result.Error!.Code.ShouldBe("hold.not_found");
    }

    [Fact]
    public async Task Release_frees_the_current_hold()
    {
        var hold = Hold();
        _store.GetUserHoldAsync(HoldTestCatalog.EventId, User, Arg.Any<CancellationToken>()).Returns(hold);

        (await new ReleaseMyHoldHandler(_catalog, _store).HandleAsync(User, HoldTestCatalog.EventId, Ct)).IsSuccess.ShouldBeTrue();

        await _store.Received(1).ReleaseHoldAsync(HoldTestCatalog.EventId, hold.HoldId, Arg.Is<IReadOnlyList<Guid>>(t => t.SequenceEqual(new[] { HoldTestCatalog.Ga })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_without_a_hold_still_succeeds()
    {
        (await new ReleaseMyHoldHandler(_catalog, _store).HandleAsync(User, HoldTestCatalog.EventId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Release_limited_to_a_hold_frees_it_when_it_is_that_hold()
    {
        var hold = Hold();
        _store.GetUserHoldAsync(HoldTestCatalog.EventId, User, Arg.Any<CancellationToken>()).Returns(hold);

        (await new ReleaseMyHoldHandler(_catalog, _store).HandleAsync(User, HoldTestCatalog.EventId, hold.HoldId, Ct)).IsSuccess.ShouldBeTrue();

        await _store.Received(1).ReleaseHoldAsync(HoldTestCatalog.EventId, hold.HoldId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_limited_to_a_hold_leaves_a_newer_hold_alone()
    {
        _store.GetUserHoldAsync(HoldTestCatalog.EventId, User, Arg.Any<CancellationToken>()).Returns(Hold());

        (await new ReleaseMyHoldHandler(_catalog, _store).HandleAsync(User, HoldTestCatalog.EventId, Guid.NewGuid(), Ct)).IsSuccess.ShouldBeTrue();

        await _store.DidNotReceive().ReleaseHoldAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_with_redis_down_is_system_busy()
    {
        _store.GetUserHoldAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InventoryUnavailableException("down"));

        (await new ReleaseMyHoldHandler(_catalog, _store).HandleAsync(User, HoldTestCatalog.EventId, Ct)).Error!.Code.ShouldBe("system.busy");
    }
}

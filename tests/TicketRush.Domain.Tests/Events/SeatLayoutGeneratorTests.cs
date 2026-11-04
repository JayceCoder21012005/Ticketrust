using System.Text.Json;
using System.Text.Json.Serialization;
using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Domain.Tests.Events;

public sealed class SeatLayoutGeneratorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<Fixture> Shared = new(LoadFixture);

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Shared.Value.Cases)
        {
            data.Add(c.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generator_matches_shared_fixture(string name)
    {
        var fixture = Shared.Value;
        var c = fixture.Cases.Single(x => x.Name == name);

        var errors = SeatLayoutGenerator.Validate(c.Definition, fixture.Tiers);

        errors.Select(e => (e.Section, e.Field)).ShouldBe(c.Expected.Errors.Select(e => (e.Section, e.Field)), ignoreOrder: true);
        if (c.Expected.Count is not { } count)
        {
            return;
        }

        var seats = SeatLayoutGenerator.Generate(c.Definition);
        seats.Count.ShouldBe(count);
        foreach (var expected in c.Expected.Seats)
        {
            seats[expected.Index].ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void RowLabel_wraps_after_Z(int index, string label) =>
        SeatLayoutGenerator.RowLabel(index).ShouldBe(label);

    [Fact]
    public void Indexes_are_contiguous_from_zero()
    {
        var c = Shared.Value.Cases.Single(x => x.Name.StartsWith("two sections", StringComparison.Ordinal));

        SeatLayoutGenerator.Generate(c.Definition).Select(s => s.Index).ShouldBe(Enumerable.Range(0, 8));
    }

    [Fact]
    public void ReplaceLayout_sets_the_layout_and_returns_its_seats()
    {
        var (evt, seated, _) = DraftWithTiers();

        var seats = evt.ReplaceLayout(Section("A", seated, rows: 20, perRow: 30));

        seats.Count.ShouldBe(600);
        evt.Layout!.Sections.ShouldHaveSingleItem().Code.ShouldBe("A");
    }

    [Fact]
    public void ReplaceLayout_with_invalid_definition_throws_layout_invalid()
    {
        var (evt, _, ga) = DraftWithTiers();

        Should.Throw<DomainException>(() => evt.ReplaceLayout(Section("A", ga, rows: 1, perRow: 1)))
            .Code.ShouldBe("layout.invalid");
        evt.Layout.ShouldBeNull();
    }

    [Fact]
    public void ReplaceLayout_after_publish_throws_not_editable()
    {
        var (evt, seated, _) = DraftWithTiers();
        evt.Publish();

        Should.Throw<DomainException>(() => evt.ReplaceLayout(Section("A", seated, rows: 1, perRow: 1)))
            .Code.ShouldBe("event.not_editable");
    }

    [Fact]
    public void Removing_a_tier_used_by_the_layout_throws_tier_in_use_naming_the_sections()
    {
        var (evt, seated, ga) = DraftWithTiers();
        evt.ReplaceLayout(Section("A", seated, rows: 1, perRow: 1));
        var gaTier = evt.Tiers.Single(t => t.Id == ga);

        var ex = Should.Throw<DomainException>(() => evt.ReplaceTiers(
            [new TierDraft(gaTier.Id, gaTier.Name, gaTier.Price, gaTier.Kind, gaTier.Capacity, gaTier.Color)]));

        ex.Code.ShouldBe("tier.in_use");
        ex.Message.ShouldContain("A");
        evt.Tiers.Count.ShouldBe(2);
    }

    private static SeatLayoutDefinition Section(string code, Guid tier, int rows, int perRow) =>
        new([new SectionDefinition(code, tier, SectionKind.Seated, rows, perRow, new LayoutPoint(100, 80))]);

    private static (Event Event, Guid Seated, Guid Ga) DraftWithTiers()
    {
        var start = new DateTimeOffset(2026, 12, 6, 12, 30, 0, TimeSpan.Zero);
        var venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);
        var evt = Event.CreateDraft(Guid.CreateVersion7(), "slug", "Title", "concert", venue, start, start.AddHours(3));
        evt.ReplaceTiers(
        [
            new TierDraft(null, "Ngồi", new Money(500_000), TicketKind.Seated, null, "#7c3aed"),
            new TierDraft(null, "Đứng", new Money(300_000), TicketKind.GeneralAdmission, 500, "#0f766e"),
        ]);
        return (evt, evt.Tiers[0].Id, evt.Tiers[1].Id);
    }

    private static Fixture LoadFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TicketRush.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("repo root (TicketRush.slnx) not found above the test binaries");
        var text = File.ReadAllText(Path.Combine(dir.FullName, "shared", "fixtures", "seat-layout.json"));
        return JsonSerializer.Deserialize<Fixture>(text, Json)!;
    }

    private sealed record Fixture(Dictionary<Guid, TicketKind> Tiers, IReadOnlyList<FixtureCase> Cases);

    private sealed record FixtureCase(string Name, SeatLayoutDefinition Definition, FixtureExpectation Expected);

    private sealed record FixtureExpectation(int? Count, IReadOnlyList<GeneratedSeat> Seats, IReadOnlyList<FixtureError> Errors);

    private sealed record FixtureError(string Section, string Field);
}

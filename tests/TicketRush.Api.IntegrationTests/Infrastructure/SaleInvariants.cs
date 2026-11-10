using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Runs <c>load-tests/sql/sale-invariants.sql</c> (spec 08 acceptance #8), the very file the k6 sale run is checked with
/// (<c>load-tests/check-sale-invariants.sh</c>), so the automated tests and the load test share one definition of "never oversold".
/// </summary>
internal static class SaleInvariants
{
    /// <summary>The checks about seats and tier counters (C1); the others are about orders and need a settled sale.</summary>
    private static readonly string[] InventoryChecks = ["seat_sold_twice", "tier_oversold", "ga_sold_count_drift"];

    internal sealed record Violation(string Check, string Subject, string Detail)
    {
        public override string ToString() => $"{Check} [{Subject}] {Detail}";
    }

    public static string Sql() => RepoFiles.Read("load-tests", "sql", "sale-invariants.sql");

    /// <summary>Every violation the query finds; an empty list means all invariants hold.</summary>
    public static async Task<IReadOnlyList<Violation>> CheckAsync(AppDbContext db, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(Sql(), connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<Violation>();
        while (await reader.ReadAsync(ct))
        {
            found.Add(new Violation(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return found;
    }

    /// <summary>Asserts only the seat and tier-counter invariants: for tests that stop mid-flight, with orders in hand-made states.</summary>
    public static async Task<int> AssertInventoryAsync(AppDbContext db, CancellationToken ct)
    {
        var broken = (await CheckAsync(db, ct)).Where(v => InventoryChecks.Contains(v.Check)).ToArray();
        broken.ShouldBeEmpty(string.Join(Environment.NewLine, broken.Select(v => v.ToString())));
        return 0;
    }
}

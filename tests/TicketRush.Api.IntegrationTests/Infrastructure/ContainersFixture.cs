using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Persistence;

[assembly: AssemblyFixture(typeof(ContainersFixture))]

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>
/// One Postgres + one Redis container per test run (no shared external DB). Each test class gets its own
/// freshly migrated database via <see cref="CreateDatabaseAsync"/>, so classes can run in parallel.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public RedisContainer Redis { get; } = new RedisBuilder("redis:7.4").Build();

    public string RedisConnectionString => Redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), Redis.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await Redis.DisposeAsync();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static AppDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options);
}

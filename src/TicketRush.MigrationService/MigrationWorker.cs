using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.MigrationService;

public sealed class MigrationOptions
{
    public const string SectionName = "Migrations";

    /// <summary>Seed demo data after migrating. Only ever enabled in Development.</summary>
    public bool SeedDevData { get; set; }
}

/// <summary>
/// One-shot job: applies EF migrations (and the dev seed), then stops the host. The api and worker wait
/// for it to complete, so schema changes never race application startup.
/// </summary>
public sealed partial class MigrationWorker(
    IServiceProvider services,
    IOptions<MigrationOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("TicketRush.Migrations");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrating database");
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync(stoppingToken);
            LogMigrated(logger);

            if (options.Value.SeedDevData)
            {
                await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(stoppingToken);
                LogSeeded(logger);
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogFailed(logger, ex);
            Environment.ExitCode = 1;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrated")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Development data seeded")]
    private static partial void LogSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

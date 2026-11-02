using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Interceptors;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Aspire resource / connection string names.</summary>
    public const string DatabaseConnectionName = "ticketrush";
    public const string RedisConnectionName = "redis";

    /// <summary>Everything api and worker need: persistence, Redis, outbox, locks and validated options.</summary>
    public static IHostApplicationBuilder AddInfrastructure(this IHostApplicationBuilder builder)
    {
        builder.AddPersistence();
        builder.AddRedisClient(RedisConnectionName);

        var services = builder.Services;
        services.AddValidatedOptions<RedisOptions>(RedisOptions.SectionName);
        services.AddValidatedOptions<JwtOptions>(JwtOptions.SectionName);
        services.AddValidatedOptions<PaymentOptions>(PaymentOptions.SectionName);
        services.AddValidatedOptions<EmailOptions>(EmailOptions.SectionName);
        services.AddValidatedOptions<StorageOptions>(StorageOptions.SectionName);
        services.AddValidatedOptions<QueueOptions>(QueueOptions.SectionName);
        services.AddValidatedOptions<HoldsOptions>(HoldsOptions.SectionName);
        services.AddValidatedOptions<RateLimitsOptions>(RateLimitsOptions.SectionName);
        services.AddValidatedOptions<FrontendOptions>(FrontendOptions.SectionName);
        services.AddValidatedOptions<OutboxOptions>(OutboxOptions.SectionName);

        services.AddSingleton<RedisKeys>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        services.AddSingleton<DomainEventTypeRegistry>();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxProcessor>();
        return builder;
    }

    /// <summary>DbContext + Identity stores only; used on its own by the migration service.</summary>
    public static IHostApplicationBuilder AddPersistence(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(DatabaseConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{DatabaseConnectionName} is not configured.");

        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuditableEntitiesInterceptor>();
        services.AddSingleton<DomainEventsToOutboxInterceptor>();

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                provider.GetRequiredService<AuditableEntitiesInterceptor>(),
                provider.GetRequiredService<DomainEventsToOutboxInterceptor>()));

        // Adds health check + OTel. Retries are off: use cases own explicit transactions and must not be replayed blindly.
        builder.EnrichNpgsqlDbContext<AppDbContext>(settings => settings.DisableRetry = true);
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        services.AddIdentityCore<AppUser>()
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<DevDataSeeder>();
        return builder;
    }

    public static IServiceCollection AddValidatedOptions<TOptions>(this IServiceCollection services, string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        return services;
    }
}

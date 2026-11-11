using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using TicketRush.Application;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Application.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Idempotency;
using TicketRush.Infrastructure.Idempotency;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Security;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Interceptors;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;
using TicketRush.Infrastructure.Storage;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Email;
using TicketRush.Infrastructure.Catalog;
using TicketRush.Application.Features.Catalog;
using TicketRush.Application.Features.Notifications;
using TicketRush.Application.Abstractions.Catalog;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;

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
        services.AddValidatedOptions<AdmissionTokenOptions>(AdmissionTokenOptions.SectionName);
        services.AddValidatedOptions<PaymentOptions>(PaymentOptions.SectionName);
        services.AddPaymentGateways(builder.Environment);
        services.AddValidatedOptions<EmailOptions>(EmailOptions.SectionName);
        services.AddValidatedOptions<StorageOptions>(StorageOptions.SectionName);
        services.AddValidatedOptions<QueueOptions>(QueueOptions.SectionName);
        services.AddValidatedOptions<HoldsOptions>(HoldsOptions.SectionName);
        services.AddValidatedOptions<RateLimitsOptions>(RateLimitsOptions.SectionName);
        services.AddValidatedOptions<FrontendOptions>(FrontendOptions.SectionName);
        services.AddValidatedOptions<FulfilmentOptions>(FulfilmentOptions.SectionName);
        services.AddValidatedOptions<RefundOptions>(RefundOptions.SectionName);
        services.AddValidatedOptions<OrderExpiryOptions>(OrderExpiryOptions.SectionName);
        services.AddValidatedOptions<CancelledEventSettlementOptions>(CancelledEventSettlementOptions.SectionName);
        services.AddValidatedOptions<OutboxOptions>(OutboxOptions.SectionName);
        services.AddValidatedOptions<EmailDeliveryOptions>(EmailDeliveryOptions.SectionName);

        services.AddSingleton<RedisKeys>();
        services.AddSingleton<IRedisPersistenceProbe, RedisPersistenceProbe>();
        services.AddHostedService<RedisDurabilityCheck>();
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        services.AddSingleton<DomainEventTypeRegistry>();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxProcessor>();

        services.AddSingleton<IAdmissionTokenFactory, AdmissionTokenFactory>();
        services.AddSingleton<IWaitingRoom, RedisWaitingRoom>();
        services.AddSingleton<IQueuePhaseSource, QueuePhaseSource>();
        services.AddScoped<ISaleWindowProvider, SaleWindowProvider>();
        services.AddSingleton<WaitingRoomOpener>();
        services.AddSingleton<WaitingRoomAdmitter>();

        services.AddScoped<IHoldCatalog, HoldCatalog>();
        services.AddSingleton<IInventoryStore, RedisInventoryStore>();
        services.AddScoped<ISoldInventorySource, DbSoldInventorySource>();
        services.AddScoped<ITierInventory, TierInventory>();
        services.AddScoped<IInventoryStrategy, GeneralAdmissionInventoryStrategy>();
        services.AddScoped<IInventoryStrategy, SeatedInventoryStrategy>();
        services.AddScoped<ITicketPurchaseLimit, TicketPurchaseLimit>();
        services.AddScoped<IEventSaleLock, EventSaleLock>();
        services.AddScoped<Application.Abstractions.Payments.IWebhookInbox, WebhookInbox>();
        services.AddSingleton<IInventoryEventSource, InventoryEventSource>();
        services.AddScoped<InventorySeeder>();
        services.AddScoped<IInventorySeeder>(sp => sp.GetRequiredService<InventorySeeder>());
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddIntegrationEventHandler<EventPublished, InventoryWarmHandler>();
        // One handler for the whole cancellation: rooms, then holds, then orders (see its remarks for why not two).
        services.AddSingleton<EventCancellationMetrics>();
        services.AddIntegrationEventHandler<EventCancelled, EventCancelledInventoryHandler>();
        // After the inventory handler: closing rooms, releasing holds and settling orders is the safety-critical part, so the
        // outbox (which stops a message at its first failing handler) runs it first.
        services.AddIntegrationEventHandler<EventCancelled, EventCancelledNotificationHandler>();
        AddOrderNotificationHandlers(services);
        // Order matters: the outbox runs a message's handlers in this order and stops at the first failure (commit first).
        services.AddIntegrationEventHandler<OrderPaid, OrderPaidInventoryCommitHandler>();
        services.AddIntegrationEventHandler<OrderPaid, OrderPaidLeaveQueueHandler>();

        PersistDataProtectionKeysToRedis(services);
        AddAccounts(services);
        AddNotifications(services);
        AddStorage(services, builder.Configuration);
        AddCatalog(services);
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
            .ReplaceService<IModelCacheKeyFactory, AppModelCacheKeyFactory>()
            .AddInterceptors(
                provider.GetRequiredService<AuditableEntitiesInterceptor>(),
                provider.GetRequiredService<DomainEventsToOutboxInterceptor>()));

        // Adds health check + OTel. Retries are off: use cases own explicit transactions and must not be replayed blindly.
        builder.EnrichNpgsqlDbContext<AppDbContext>(settings => settings.DisableRetry = true);
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());

        // UserManager resolves its token providers on construction, so every host with Identity needs Data Protection.
        services.AddDataProtection().SetApplicationName("TicketRush");
        services.AddIdentityCore<AppUser>(ConfigureIdentity)
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddTokenProvider<EmailConfirmationTokenProvider>(EmailConfirmationTokenProvider.ProviderName)
            .AddPasswordValidator<LetterAndNotEmailPasswordValidator>();
        // Reset links expire after 1 hour (spec 09); confirmation links use their own 3-day provider.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromHours(1));
        // The seeder hashes the demo presale code, so every host with persistence needs the pepper.
        services.AddValidatedOptions<CatalogOptions>(CatalogOptions.SectionName);
        services.AddSingleton<IAccessCodeHasher, AccessCodeHasher>();
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

    /// <summary>Spec 03: 10+ characters with a digit (letter and not-the-email are a custom validator), 5 failures lock 15 min.</summary>
    private static void ConfigureIdentity(IdentityOptions options)
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenProvider.ProviderName;
    }

    /// <summary>Keys live in Redis so every api and worker instance can read tokens protected by another.</summary>
    private static void PersistDataProtectionKeysToRedis(IServiceCollection services)
    {
        services.AddOptions<KeyManagementOptions>()
            .Configure<IConnectionMultiplexer, RedisKeys>((options, redis, keys) =>
                options.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(), $"{keys.Prefix}dataprotection:keys"));
    }

    private static void AddAccounts(IServiceCollection services)
    {
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IEmailRenderer, ScribanEmailRenderer>();
        services.AddIntegrationEventHandler<UserRegistered, SendConfirmationEmailHandler>();
        services.AddIntegrationEventHandler<PasswordResetRequested, SendPasswordResetEmailHandler>();
    }

    /// <summary>
    /// Registered before the inventory and queue consumers of <c>OrderPaid</c>: the outbox runs a message's handlers in registration
    /// order and stops at the first failure, so a failing Redis step must never hold back the buyer's notice.
    /// <para>
    /// Accepted trade-off: the reverse also holds, so a deterministic failure inside a notification handler (a DomainException from
    /// <c>Notification.Create</c>, an email model that cannot be serialized) would hold back the inventory commit through the retries
    /// until the message is dead-lettered. The risk is low: titles, bodies and links are built from bounded values (an event title is
    /// at most 200 characters, the link is a fixed path) and live pushes are swallowed. The commit is also recoverable by the reconciler.
    /// </para>
    /// </summary>
    private static void AddOrderNotificationHandlers(IServiceCollection services)
    {
        services.AddIntegrationEventHandler<OrderPaid, OrderPaidNotificationHandler>();
        services.AddIntegrationEventHandler<OrderExpired, OrderExpiredNotificationHandler>();
        services.AddIntegrationEventHandler<RefundRequested, RefundRequestedNotificationHandler>();
        services.AddIntegrationEventHandler<OrderRefunded, OrderRefundedNotificationHandler>();
        services.AddIntegrationEventHandler<PaymentRefunded, PaymentRefundedNotificationHandler>();
    }

    /// <summary>Spec 09: the notification writer and the email delivery worker's processor.</summary>
    private static void AddNotifications(IServiceCollection services)
    {
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<INotificationWriter, NotificationWriter>();
        services.AddSingleton<EmailDeliveryProcessor>();
        // IUserNotifier comes from AddTicketRushSignalR, which both hosts call.
    }

    /// <summary>In-process HybridCache (no L2 yet): single-flight per key protects Postgres from stampedes (C6).</summary>
    private static void AddCatalog(IServiceCollection services)
    {
        // Seat maps of large venues are a few MB of JSON; the 1 MB default would silently skip caching them.
        services.AddHybridCache(options => options.MaximumPayloadBytes = 16 * 1024 * 1024);
        services.AddScoped<EventCatalogReader>();

        // Spec 05: after publish / cancel / edit, the worker refreshes the web's ISR pages through the outbox.
        services.AddHttpClient<IWebCacheRevalidator, WebCacheRevalidator>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddIntegrationEventHandler<EventPublished, CatalogRevalidationHandler>();
        services.AddIntegrationEventHandler<EventCancelled, CatalogRevalidationHandler>();
        services.AddIntegrationEventHandler<EventDetailsChanged, CatalogRevalidationHandler>();
        services.AddScoped<CachedEventCatalogReader>();
        services.AddScoped<IEventCatalogReader>(sp => sp.GetRequiredService<CachedEventCatalogReader>());
        services.AddScoped<ICatalogCacheInvalidator>(sp => sp.GetRequiredService<CachedEventCatalogReader>());
    }

    private static void AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[$"{StorageOptions.SectionName}:Provider"];
        if (string.Equals(provider, "S3", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Storage:Provider S3 is not implemented yet; use Local.");
        }

        services.AddSingleton<LocalFileStorage>();
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalFileStorage>());
    }
}

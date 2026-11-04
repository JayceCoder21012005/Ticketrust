using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Catalog;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Persistence;

/// <param name="options">EF options.</param>
/// <param name="dataProtection">
/// Encrypts ticket QR secrets at rest. Optional only so design-time and migration-only contexts can be built without
/// Data Protection; saving or reading a ticket without it throws.
/// </param>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IDataProtectionProvider? dataProtection = null)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    /// <summary>Shadow property mapped to Postgres' system column <c>xmin</c> (optimistic concurrency).</summary>
    public const string RowVersionProperty = "Version";

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<EventSeat> EventSeats => Set<EventSeat>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<ProcessedWebhook> ProcessedWebhooks => Set<ProcessedWebhook>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ConsumerReceipt> ConsumerReceipts => Set<ConsumerReceipt>();

    /// <summary>Part of the model cache key: the converter below captures this provider's protector.</summary>
    internal IDataProtectionProvider? DataProtection => dataProtection;

    Task<int> IAppDbContext.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("unaccent").HasPostgresExtension("pg_trgm");
        builder.HasDbFunction(typeof(CatalogDbFunctions).GetMethod(nameof(CatalogDbFunctions.Matches))!).HasName("catalog_matches");
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        RenameIdentityTables(builder);
        MapRowVersionOnAggregates(builder);
        MapTicketSecretEncryption(builder);
    }

    private void MapTicketSecretEncryption(ModelBuilder builder) =>
        builder.Entity<Ticket>().Property(t => t.QrSecret).HasConversion(new ProtectedBytesConverter(TicketSecretProtector.Create(dataProtection)));

    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>().ToTable("users").Ignore(u => u.DomainEvents);
        builder.Entity<AppRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
    }

    private static void MapRowVersionOnAggregates(ModelBuilder builder)
    {
        var aggregates = builder.Model.GetEntityTypes()
            .Where(type => type.ClrType.IsAssignableTo(typeof(AggregateRoot)) && !type.IsOwned())
            .Select(type => type.ClrType)
            .ToList();

        foreach (var clrType in aggregates)
        {
            builder.Entity(clrType)
                .Property<uint>(RowVersionProperty)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
    }
}

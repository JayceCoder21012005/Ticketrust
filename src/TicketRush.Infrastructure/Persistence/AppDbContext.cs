using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Catalog;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    /// <summary>Shadow property mapped to Postgres' system column <c>xmin</c> (optimistic concurrency).</summary>
    public const string RowVersionProperty = "Version";

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<EventSeat> EventSeats => Set<EventSeat>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ConsumerReceipt> ConsumerReceipts => Set<ConsumerReceipt>();

    Task<int> IAppDbContext.SaveChangesAsync(CancellationToken ct) => SaveChangesAsync(ct);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("unaccent").HasPostgresExtension("pg_trgm");
        builder.HasDbFunction(typeof(CatalogDbFunctions).GetMethod(nameof(CatalogDbFunctions.Matches))!).HasName("catalog_matches");
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        RenameIdentityTables(builder);
        MapRowVersionOnAggregates(builder);
    }

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

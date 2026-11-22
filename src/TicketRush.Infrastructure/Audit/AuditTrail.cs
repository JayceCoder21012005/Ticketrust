using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketRush.Application.Abstractions.Audit;
using TicketRush.Domain.Audit;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Audit;

/// <summary>The system acts (worker jobs, outbox consumers, tests without a request): no actor, no IP, no correlation id.</summary>
public sealed class SystemAuditContext : IAuditContext
{
    public Guid? ActorUserId => null;

    public string? ActorIp => null;

    public string? CorrelationId => null;
}

internal static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string? Serialize(object? value) => value is null ? null : JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// The automatic half of spec 16's audit log: before EF saves, every added, modified or deleted entity whose class is
/// <see cref="AuditedAttribute">[Audited]</see> becomes an <see cref="AuditEntry"/> in the SAME save (so it commits with the change).
/// Field-level before/after values; timestamps, the row version and <see cref="SensitiveAttribute">[Sensitive]</see> properties
/// are left out. An update that changed none of the audited properties leaves no entry.
/// </summary>
public sealed class AuditTrailInterceptor(IAuditContext actor, TimeProvider time) : SaveChangesInterceptor
{
    private static readonly HashSet<string> Noise = [nameof(IAuditable.CreatedAt), nameof(IAuditable.UpdatedAt)];

    private static readonly ConcurrentDictionary<Type, Shape?> Shapes = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private sealed record Shape(string Name, HashSet<string>? Only, HashSet<string> Sensitive, bool Creates);

    private static Shape? ShapeOf(Type type) => Shapes.GetOrAdd(type, static t =>
    {
        if (t.GetCustomAttribute<AuditedAttribute>() is not { } audited)
        {
            return null;
        }

        var sensitive = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SensitiveAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet();
        return new Shape(ToSnake(t.Name), audited.Properties.Count > 0 ? [.. audited.Properties] : null, sensitive, audited.Creates);
    });

    private void Record(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = time.GetUtcNow();
        var entries = new List<AuditEntry>();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) || ShapeOf(entry.Entity.GetType()) is not { } shape
                || (entry.State == EntityState.Added && !shape.Creates))
            {
                continue;
            }

            var changes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in entry.Properties)
            {
                Collect(changes, entry.State, shape, property.Metadata.Name, property, property.Metadata.IsShadowProperty());
            }

            foreach (var complex in entry.ComplexProperties)
            {
                foreach (var property in complex.Properties)
                {
                    Collect(changes, entry.State, shape, $"{complex.Metadata.Name}.{property.Metadata.Name}", property, false, complex.Metadata.Name);
                }
            }

            if (entry.State == EntityState.Modified && changes.Count == 0)
            {
                continue;
            }

            var action = entry.State switch
            {
                EntityState.Added => "created",
                EntityState.Deleted => "deleted",
                _ => "updated",
            };
            entries.Add(AuditEntry.Create(now, actor.ActorUserId, actor.ActorIp, $"{shape.Name}.{action}", shape.Name, KeyOf(entry), AuditJson.Serialize(changes), actor.CorrelationId));
        }

        if (entries.Count > 0)
        {
            context.Set<AuditEntry>().AddRange(entries);
        }
    }

    private static void Collect(
        SortedDictionary<string, object?> changes, EntityState state, Shape shape, string name, PropertyEntry property, bool shadow, string? owner = null)
    {
        var root = owner ?? name;
        if (shadow || Noise.Contains(root) || shape.Sensitive.Contains(root) || (shape.Only is { } only && !only.Contains(root)))
        {
            return;
        }

        switch (state)
        {
            case EntityState.Added when property.CurrentValue is not null:
                changes[ToCamel(name)] = new { before = (object?)null, after = property.CurrentValue };
                break;
            case EntityState.Deleted:
                changes[ToCamel(name)] = new { before = property.OriginalValue, after = (object?)null };
                break;
            case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                changes[ToCamel(name)] = new { before = property.OriginalValue, after = property.CurrentValue };
                break;
        }
    }

    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        return key is null ? null : string.Join(":", key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString()));
    }

    private static string ToCamel(string name) =>
        string.Join('.', name.Split('.').Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));

    internal static string ToSnake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>The explicit half of the audit log (security events, admin actions).</summary>
public sealed class AuditLogger(AppDbContext db, IAuditContext actor, TimeProvider time) : IAuditLogger
{
    public void Record(string action, string entityType, string? entityId, object? details = null) =>
        db.Set<AuditEntry>().Add(AuditEntry.Create(time.GetUtcNow(), actor.ActorUserId, actor.ActorIp, action, entityType, entityId, AuditJson.Serialize(details), actor.CorrelationId));

    public async Task LogAsync(string action, string entityType, string? entityId, object? details, CancellationToken ct)
    {
        Record(action, entityType, entityId, details);
        await db.SaveChangesAsync(ct);
    }
}

namespace TicketRush.Domain.Audit;

/// <summary>
/// One row of <c>audit_entries</c> (spec 16): append-only. The database refuses UPDATE, DELETE and TRUNCATE on the table (trigger), so
/// nothing here has a setter after creation. <see cref="Changes"/> is field-level <c>{ "field": { "before": …, "after": … } }</c>
/// JSON (or an action's own details); <c>[Sensitive]</c> values never reach it.
/// </summary>
public sealed class AuditEntry
{
    public const int ActionMaxLength = 64;

    private AuditEntry(DateTimeOffset at, Guid? actorUserId, string? actorIp, string action, string entityType, string? entityId, string? changes, string? correlationId)
    {
        Id = Guid.CreateVersion7();
        At = at;
        ActorUserId = actorUserId;
        ActorIp = actorIp;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Changes = changes;
        CorrelationId = correlationId;
    }

    // Required by EF Core.
    private AuditEntry()
    {
        Action = string.Empty;
        EntityType = string.Empty;
    }

    public Guid Id { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>Null for the system (worker jobs, outbox consumers) and for anonymous requests (a failed login).</summary>
    public Guid? ActorUserId { get; private set; }

    public string? ActorIp { get; private set; }

    /// <summary><c>entity.created</c> / <c>entity.updated</c> / <c>entity.deleted</c>, or an explicit action such as <c>role.granted</c>.</summary>
    public string Action { get; private set; }

    public string EntityType { get; private set; }

    public string? EntityId { get; private set; }

    /// <summary>JSON (jsonb).</summary>
    public string? Changes { get; private set; }

    public string? CorrelationId { get; private set; }

    public static AuditEntry Create(
        DateTimeOffset at, Guid? actorUserId, string? actorIp, string action, string entityType, string? entityId, string? changes, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        return new AuditEntry(at, actorUserId, actorIp, action.Length > ActionMaxLength ? action[..ActionMaxLength] : action, entityType, entityId, changes, correlationId);
    }
}

/// <summary>
/// The class is recorded by the audit interceptor on create, update and delete (spec 16). With property names, only changes of
/// those properties are recorded (and an update touching none of them leaves no entry).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuditedAttribute(params string[] properties) : Attribute
{
    public IReadOnlyList<string> Properties { get; } = properties;

    /// <summary>False: creations are not recorded (rows made in bulk on the hot path, such as tickets and orders), only changes.</summary>
    public bool Creates { get; init; } = true;
}

/// <summary>Never written to the audit log (secrets, hashes, tokens), whatever the class's <see cref="AuditedAttribute"/> says.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;

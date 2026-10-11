namespace Erp.BuildingBlocks.Domain;

public interface ITenantScoped { Guid TenantId { get; set; } }
public interface ISoftDeletable { bool IsDeleted { get; set; } DateTime? DeletedAtUtc { get; set; } }
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTime? ModifiedAtUtc { get; set; }
    string? ModifiedBy { get; set; }
}
/// <summary>Marker: changes to this entity are not written to the audit trail (outbox, inbox, the audit log itself).</summary>
public interface INotAudited { }

/// <summary>Base type of every message that crosses a service boundary. Raised by aggregates, persisted to the outbox atomically with the data change.</summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
    public Guid TenantId { get; set; }
}

public abstract class Entity
{
    private readonly List<IntegrationEvent> _events = new();
    public Guid Id { get; protected set; } = Guid.NewGuid();
    protected void Raise(IntegrationEvent e) => _events.Add(e);
    public IReadOnlyList<IntegrationEvent> PopEvents() { var copy = _events.ToArray(); _events.Clear(); return copy; }
}

/// <summary>Non-tenant data (platform level). Audited, soft-deletable, optimistic concurrency token.</summary>
public abstract class AuditableEntity : Entity, IAuditable, ISoftDeletable
{
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public byte[] RowVersion { get; private set; } = [];
}

/// <summary>Tenant-owned data. TenantId is stamped on insert and enforced by a global query filter.</summary>
public abstract class TenantEntity : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }
}

public class DomainException(string message) : Exception(message);
public class NotFoundException(string entity, object key) : Exception($"{entity} '{key}' was not found.");
public class ConflictException(string message) : Exception(message);
public class ForbiddenException(string message) : Exception(message);

public static class Guard
{
    public static decimal Positive(decimal v, string name) => v > 0 ? v : throw new DomainException($"{name} must be greater than zero.");
    public static decimal NotNegative(decimal v, string name) => v >= 0 ? v : throw new DomainException($"{name} cannot be negative.");
    public static string NotBlank(string? v, string name) => string.IsNullOrWhiteSpace(v) ? throw new DomainException($"{name} is required.") : v.Trim();
}

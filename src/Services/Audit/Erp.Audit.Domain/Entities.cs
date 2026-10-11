using System.Security.Cryptography;
using System.Text;
namespace Erp.Audit.Domain;

/// <summary>
/// Append-only, hash-chained audit record. Hash = SHA-256(previousHash | fields). Changing or deleting any row breaks the chain
/// from that row onward, which GET /api/audit/verify detects. Not a TenantEntity: the chain is per tenant but rows are never filtered away.
/// </summary>
public sealed class AuditLogEntry : INotAudited
{
    public const string Genesis = "GENESIS";
    private AuditLogEntry() { }
    public AuditLogEntry(Guid eventId, Guid tenantId, string service, string entityType, string entityId, string action,
        string? changesJson, Guid? userId, string? userName, string? clientIp, DateTime occurredAtUtc, string previousHash)
    {
        EventId = eventId; TenantId = tenantId; Service = service; EntityType = entityType; EntityId = entityId; Action = action;
        ChangesJson = changesJson; UserId = userId; UserName = userName; ClientIp = clientIp; OccurredAtUtc = occurredAtUtc;
        PreviousHash = previousHash; Hash = ComputeHash();
    }
    public long Sequence { get; private set; }
    public Guid EventId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Service { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    public string Action { get; private set; } = "";
    public string? ChangesJson { get; private set; }
    public Guid? UserId { get; private set; }
    public string? UserName { get; private set; }
    public string? ClientIp { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string PreviousHash { get; private set; } = Genesis;
    public string Hash { get; private set; } = "";

    public string ComputeHash()
    {
        var text = $"{PreviousHash}|{TenantId}|{Service}|{EntityType}|{EntityId}|{Action}|{ChangesJson}|{UserId}|{UserName}|{ClientIp}|{OccurredAtUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

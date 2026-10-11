namespace Erp.Audit.Application;

public interface IAuditDbContext : IUnitOfWork { DbSet<AuditLogEntry> Entries { get; } }

/// <summary>Every service publishes AuditRecordedEvent through its outbox; this appends it to the tenant's hash chain.</summary>
internal sealed class AppendAuditHandler(IAuditDbContext db) : IIntegrationEventHandler<AuditRecordedEvent>
{
    public async Task Handle(AuditRecordedEvent e, CancellationToken ct)
    {
        if (await db.Entries.AnyAsync(x => x.EventId == e.EventId, ct)) return;      // duplicate delivery
        var prev = await db.Entries.Where(x => x.TenantId == e.TenantId).OrderByDescending(x => x.Sequence)
            .Select(x => x.Hash).FirstOrDefaultAsync(ct) ?? AuditLogEntry.Genesis;
        db.Entries.Add(new AuditLogEntry(e.EventId, e.TenantId, e.Service, e.EntityType, e.EntityId, e.Action, e.ChangesJson,
            e.UserId, e.UserName, e.ClientIp, e.OccurredAtUtc, prev));
    }
}

public sealed record AuditEntryDto(long Sequence, Guid EventId, string Service, string EntityType, string EntityId, string Action,
    string? ChangesJson, Guid? UserId, string? UserName, string? ClientIp, DateTime OccurredAtUtc, string Hash);
public sealed record SearchAuditQuery(string? EntityType, string? EntityId, string? Action, Guid? UserId, DateTime? From, DateTime? To, int Page, int PageSize)
    : IQuery<PagedResult<AuditEntryDto>>;

internal sealed class SearchAuditHandler(IAuditDbContext db, ITenantContext tenant) : IQueryHandler<SearchAuditQuery, PagedResult<AuditEntryDto>>
{
    public async Task<PagedResult<AuditEntryDto>> Handle(SearchAuditQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 1, 500);
        var tid = tenant.TenantId;
        var query = db.Entries.AsNoTracking().Where(x => x.TenantId == tid);
        if (q.EntityType != null) query = query.Where(x => x.EntityType == q.EntityType);
        if (q.EntityId != null) query = query.Where(x => x.EntityId == q.EntityId);
        if (q.Action != null) query = query.Where(x => x.Action == q.Action);
        if (q.UserId != null) query = query.Where(x => x.UserId == q.UserId);
        if (q.From != null) query = query.Where(x => x.OccurredAtUtc >= q.From);
        if (q.To != null) query = query.Where(x => x.OccurredAtUtc <= q.To);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.Sequence).Skip((page - 1) * size).Take(size)
            .Select(x => new AuditEntryDto(x.Sequence, x.EventId, x.Service, x.EntityType, x.EntityId, x.Action, x.ChangesJson, x.UserId, x.UserName, x.ClientIp, x.OccurredAtUtc, x.Hash))
            .ToListAsync(ct);
        return new PagedResult<AuditEntryDto>(items, page, size, total);
    }
}

public sealed record ChainVerification(bool IsValid, long CheckedEntries, long? FirstBrokenSequence, string? Reason);
public sealed record VerifyChainQuery() : IQuery<ChainVerification>;

internal sealed class VerifyChainHandler(IAuditDbContext db, ITenantContext tenant) : IQueryHandler<VerifyChainQuery, ChainVerification>
{
    public async Task<ChainVerification> Handle(VerifyChainQuery q, CancellationToken ct)
    {
        var tid = tenant.TenantId; var expectedPrev = AuditLogEntry.Genesis; long n = 0;
        await foreach (var e in db.Entries.AsNoTracking().Where(x => x.TenantId == tid).OrderBy(x => x.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            n++;
            if (e.PreviousHash != expectedPrev) return new(false, n, e.Sequence, "Previous-hash link broken (row removed or reordered).");
            if (e.ComputeHash() != e.Hash) return new(false, n, e.Sequence, "Row content does not match its hash (row modified).");
            expectedPrev = e.Hash;
        }
        return new(true, n, null, null);
    }
}

using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
namespace Erp.BuildingBlocks.Infrastructure;

public sealed record ServiceInfo(string Name);

public sealed class OutboxMessage : INotAudited
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public Guid TenantId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

public sealed class InboxMessage : INotAudited
{
    public Guid EventId { get; set; }
    public string Handler { get; set; } = "";
    public DateTime ProcessedAtUtc { get; set; }
}

/// <summary>
/// Base for every service DbContext. Provides, uniformly:
///  - tenant isolation (global query filter + TenantId stamping),
///  - soft delete + created/modified stamps + optimistic concurrency,
///  - transactional outbox (domain events + audit events are written in the same transaction as the data),
///  - inbox table for idempotent consumers.
/// </summary>
public abstract class ErpDbContext : DbContext, IUnitOfWork
{
    private static readonly MethodInfo ApplyFilterMethod =
        typeof(ErpDbContext).GetMethod(nameof(ApplyFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly string[] Sensitive = ["password", "secret", "token", "hash"];
    private static readonly string[] Noise = ["RowVersion", "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy", "DeletedAtUtc"];

    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _user;
    private readonly ServiceInfo _service;

    protected ErpDbContext(DbContextOptions options, ITenantContext tenant, ICurrentUser user, ServiceInfo service) : base(options)
    { _tenant = tenant; _user = user; _service = service; }

    /// <summary>Read by the global query filters at query time (EF parameterises it per context instance).</summary>
    public Guid CurrentTenantId => _tenant.TenantId;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected abstract void ConfigureModel(ModelBuilder modelBuilder);

    protected sealed override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Payload).HasColumnType("nvarchar(max)");
            b.Property(x => x.Error).HasMaxLength(2000);
            b.HasIndex(x => new { x.ProcessedAtUtc, x.OccurredAtUtc });
        });
        mb.Entity<InboxMessage>(b => { b.ToTable("InboxMessages"); b.HasKey(x => new { x.EventId, x.Handler }); });

        ConfigureModel(mb);

        foreach (var et in mb.Model.GetEntityTypes().ToList())
        {
            // Client-generated GUID keys must never be treated as "store generated" (otherwise new children of tracked parents are seen as Modified).
            foreach (var p in et.FindPrimaryKey()?.Properties ?? [])
                if (p.ClrType == typeof(Guid)) p.ValueGenerated = ValueGenerated.Never;

            if (et.IsOwned() || et.BaseType != null) continue;
            var t = et.ClrType;
            if (typeof(AuditableEntity).IsAssignableFrom(t))
                mb.Entity(t).Property(nameof(AuditableEntity.RowVersion)).IsRowVersion();
            var tenant = typeof(ITenantScoped).IsAssignableFrom(t);
            var soft = typeof(ISoftDeletable).IsAssignableFrom(t);
            if (tenant || soft) ApplyFilterMethod.MakeGenericMethod(t).Invoke(this, [mb, tenant, soft]);
        }
    }

    private void ApplyFilter<T>(ModelBuilder mb, bool tenant, bool soft) where T : class
    {
        Expression<Func<T, bool>> f;
        if (tenant && soft) f = e => ((ITenantScoped)e).TenantId == CurrentTenantId && !((ISoftDeletable)e).IsDeleted;
        else if (tenant) f = e => ((ITenantScoped)e).TenantId == CurrentTenantId;
        else f = e => !((ISoftDeletable)e).IsDeleted;
        mb.Entity<T>().HasQueryFilter(f);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<decimal>().HavePrecision(18, 4);
        cb.Properties<string>().HaveMaxLength(256);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    { PrepareForSave(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { PrepareForSave(); return base.SaveChanges(acceptAllChangesOnSuccess); }

    private void PrepareForSave()
    {
        ChangeTracker.DetectChanges();
        var now = DateTime.UtcNow;
        var userName = _user.UserName;
        var outgoing = new List<IntegrationEvent>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is Entity domainEntity) outgoing.AddRange(domainEntity.PopEvents());
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is INotAudited) continue;

            var action = entry.State.ToString();

            if (entry.Entity is ITenantScoped ts && entry.State == EntityState.Added)
            {
                if (ts.TenantId == Guid.Empty) ts.TenantId = _tenant.TenantId;
                if (ts.TenantId == Guid.Empty) throw new InvalidOperationException($"No tenant in context while inserting {entry.Metadata.ClrType.Name}.");
            }
            if (entry.Entity is IAuditable a)
            {
                if (entry.State == EntityState.Added) { a.CreatedAtUtc = now; a.CreatedBy = userName; }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Property(nameof(IAuditable.ModifiedAtUtc)).CurrentValue = now;
                    entry.Property(nameof(IAuditable.ModifiedBy)).CurrentValue = userName;
                }
            }
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable)
            {
                entry.State = EntityState.Unchanged;
                entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
                entry.Property(nameof(ISoftDeletable.DeletedAtUtc)).CurrentValue = now;
                action = "Deleted";
            }

            var audit = BuildAudit(entry, action);
            if (audit != null) outgoing.Add(audit);
        }

        foreach (var ev in outgoing)
        {
            if (ev.TenantId == Guid.Empty) ev.TenantId = _tenant.TenantId;
            OutboxMessages.Add(new OutboxMessage
            {
                Id = ev.EventId,
                Type = ev.GetType().Name,
                Payload = JsonSerializer.Serialize(ev, ev.GetType(), JsonDefaults.Options),
                OccurredAtUtc = ev.OccurredAtUtc,
                TenantId = ev.TenantId
            });
        }
    }

    private AuditRecordedEvent? BuildAudit(EntityEntry entry, string action)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            var n = p.Metadata.Name;
            if (p.Metadata.IsShadowProperty()) continue;
            if (Noise.Contains(n) && n != nameof(ISoftDeletable.DeletedAtUtc)) continue;
            if (n == nameof(ISoftDeletable.DeletedAtUtc)) continue;
            object? Val(object? v) => Sensitive.Any(s => n.Contains(s, StringComparison.OrdinalIgnoreCase)) ? "***" : v;
            switch (entry.State)
            {
                case EntityState.Added: changes[n] = Val(p.CurrentValue); break;
                case EntityState.Deleted: changes[n] = Val(p.OriginalValue); break;
                case EntityState.Modified when p.IsModified:
                    changes[n] = new { Old = Val(p.OriginalValue), New = Val(p.CurrentValue) }; break;
            }
        }
        if (entry.State == EntityState.Modified && changes.Count == 0) return null;

        var key = string.Join(",", entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue));
        var tenantId = entry.Entity is ITenantScoped t ? t.TenantId : _tenant.TenantId;
        return new AuditRecordedEvent(_service.Name, entry.Metadata.ClrType.Name, key, action,
            JsonSerializer.Serialize(changes, JsonDefaults.Options), _user.UserId, _user.UserName, null)
        { TenantId = tenantId };
    }
}

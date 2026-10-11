namespace Erp.Audit.Infrastructure;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IAuditDbContext
{
    public DbSet<AuditLogEntry> Entries => Set<AuditLogEntry>();

    protected override void ConfigureModel(ModelBuilder mb) =>
        mb.Entity<AuditLogEntry>(b =>
        {
            b.ToTable("AuditLog");
            b.HasKey(x => x.Sequence);
            b.Property(x => x.Sequence).ValueGeneratedOnAdd();
            b.Property(x => x.ChangesJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.EntityId).HasMaxLength(200);
            b.Property(x => x.Hash).HasMaxLength(64); b.Property(x => x.PreviousHash).HasMaxLength(64);
            b.HasIndex(x => x.EventId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.PreviousHash }).IsUnique();      // forks of the chain are rejected by the database
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            b.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });

    // Application-level immutability. The database-level guard is scripts/sql/audit-immutability.sql.
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        if (ChangeTracker.Entries<AuditLogEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("The audit log is append-only.");
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}

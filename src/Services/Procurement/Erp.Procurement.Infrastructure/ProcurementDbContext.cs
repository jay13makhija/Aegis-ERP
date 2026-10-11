namespace Erp.Procurement.Infrastructure;

public sealed class ProcurementDbContext(DbContextOptions<ProcurementDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IProcurementDbContext
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<Supplier>(b => { b.ToTable("Suppliers"); b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0"); });
        mb.Entity<PurchaseOrder>(b =>
        {
            b.ToTable("PurchaseOrders");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Currency).HasMaxLength(3);
            b.Property(x => x.DecisionNote).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("PurchaseOrderLines");
                l.WithOwner().HasForeignKey("PurchaseOrderId");
                l.HasKey(x => x.Id);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}

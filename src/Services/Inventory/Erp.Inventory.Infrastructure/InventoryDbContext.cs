namespace Erp.Inventory.Infrastructure;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IInventoryDbContext
{
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<Warehouse>(b => { b.ToTable("Warehouses"); b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsDeleted] = 0"); });
        mb.Entity<Product>(b => { b.ToTable("Products"); b.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique().HasFilter("[IsDeleted] = 0"); b.Property(x => x.Description).HasMaxLength(2000); });
        mb.Entity<StockLevel>(b => { b.ToTable("StockLevels"); b.HasIndex(x => new { x.TenantId, x.ProductId, x.WarehouseId }).IsUnique(); });
        mb.Entity<StockMovement>(b =>
        {
            b.ToTable("StockMovements");
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Note).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.ProductId, x.OccurredAtUtc });
        });
        mb.Entity<Shipment>(b =>
        {
            b.ToTable("Shipments");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.TrackingNumber }).IsUnique().HasFilter("[IsDeleted] = 0");
            b.OwnsMany(x => x.History, h =>
            {
                h.ToTable("ShipmentEvents");
                h.WithOwner().HasForeignKey("ShipmentId");
                h.HasKey(x => x.Id);
                h.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            });
            b.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}

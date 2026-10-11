namespace Erp.Inventory.Application;

public interface IInventoryDbContext : IUnitOfWork
{
    DbSet<Warehouse> Warehouses { get; }
    DbSet<Product> Products { get; }
    DbSet<StockLevel> StockLevels { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<Shipment> Shipments { get; }
}

/// <summary>Pushes committed stock changes to connected clients (SignalR in the API project).</summary>
public interface IStockNotifier { Task NotifyAsync(StockMovedEvent e, CancellationToken ct); }

public interface IStockService
{
    Task<StockMovement> ApplyAsync(Guid productId, Guid warehouseId, MovementType type, decimal quantity, decimal? unitCost,
        string? referenceType, Guid? referenceId, string? note, CancellationToken ct);
}

/// <summary>The only place balances change. Updates the level, appends the ledger row and raises events — all committed in one transaction by the caller.</summary>
public sealed class StockService(IInventoryDbContext db) : IStockService
{
    public async Task<StockMovement> ApplyAsync(Guid productId, Guid warehouseId, MovementType type, decimal quantity, decimal? unitCost,
        string? referenceType, Guid? referenceId, string? note, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct) ?? throw new NotFoundException(nameof(Product), productId);
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new NotFoundException(nameof(Warehouse), warehouseId);

        var level = db.StockLevels.Local.FirstOrDefault(l => l.ProductId == productId && l.WarehouseId == warehouseId)
                    ?? await db.StockLevels.FirstOrDefaultAsync(l => l.ProductId == productId && l.WarehouseId == warehouseId, ct);
        if (level is null) { level = new StockLevel(productId, warehouseId); db.StockLevels.Add(level); }

        decimal cost;
        var inbound = type is MovementType.Receipt or MovementType.TransferIn or MovementType.AdjustmentIn;
        if (inbound) { cost = unitCost ?? (level.AverageCost > 0 ? level.AverageCost : product.StandardCost); level.Receive(quantity, cost); }
        else { cost = level.AverageCost; level.Issue(quantity); }

        var movement = new StockMovement(productId, product.Sku, warehouseId, type, quantity, cost, level.OnHand, referenceType, referenceId, note);
        db.StockMovements.Add(movement);
        if (!inbound) level.CheckReorder(product);
        return movement;
    }
}

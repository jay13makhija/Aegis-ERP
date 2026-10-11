namespace Erp.Inventory.Application;

public sealed record ReceiveStockCommand(Guid ProductId, Guid WarehouseId, decimal Quantity, decimal? UnitCost, string? Reference) : ICommand<Guid>;
public sealed record IssueStockCommand(Guid ProductId, Guid WarehouseId, decimal Quantity, string? Reference) : ICommand<Guid>;
public sealed record TransferStockCommand(Guid ProductId, Guid FromWarehouseId, Guid ToWarehouseId, decimal Quantity, string? Note) : ICommand<Guid>;
public sealed record AdjustStockCommand(Guid ProductId, Guid WarehouseId, decimal NewOnHand, string Reason) : ICommand<Guid?>;
public sealed record ReserveStockCommand(Guid ProductId, Guid WarehouseId, decimal Quantity, bool Release) : ICommand<decimal>;

internal sealed class ReceiveStockHandler(IStockService stock) : ICommandHandler<ReceiveStockCommand, Guid>
{
    public async Task<Guid> Handle(ReceiveStockCommand c, CancellationToken ct) =>
        (await stock.ApplyAsync(c.ProductId, c.WarehouseId, MovementType.Receipt, c.Quantity, c.UnitCost, "Manual", null, c.Reference, ct)).Id;
}

internal sealed class IssueStockHandler(IStockService stock) : ICommandHandler<IssueStockCommand, Guid>
{
    public async Task<Guid> Handle(IssueStockCommand c, CancellationToken ct) =>
        (await stock.ApplyAsync(c.ProductId, c.WarehouseId, MovementType.Issue, c.Quantity, null, "Manual", null, c.Reference, ct)).Id;
}

internal sealed class TransferStockHandler(IStockService stock) : ICommandHandler<TransferStockCommand, Guid>
{
    public async Task<Guid> Handle(TransferStockCommand c, CancellationToken ct)
    {
        if (c.FromWarehouseId == c.ToWarehouseId) throw new DomainException("Source and destination warehouse must differ.");
        var transferId = Guid.NewGuid();
        var outMove = await stock.ApplyAsync(c.ProductId, c.FromWarehouseId, MovementType.TransferOut, c.Quantity, null, "Transfer", transferId, c.Note, ct);
        await stock.ApplyAsync(c.ProductId, c.ToWarehouseId, MovementType.TransferIn, c.Quantity, outMove.UnitCost, "Transfer", transferId, c.Note, ct);
        return transferId;
    }
}

internal sealed class AdjustStockHandler(IInventoryDbContext db, IStockService stock) : ICommandHandler<AdjustStockCommand, Guid?>
{
    public async Task<Guid?> Handle(AdjustStockCommand c, CancellationToken ct)
    {
        Guard.NotNegative(c.NewOnHand, nameof(c.NewOnHand));
        var current = await db.StockLevels.Where(l => l.ProductId == c.ProductId && l.WarehouseId == c.WarehouseId).Select(l => l.OnHand).FirstOrDefaultAsync(ct);
        var diff = c.NewOnHand - current;
        if (diff == 0) return null;
        var type = diff > 0 ? MovementType.AdjustmentIn : MovementType.AdjustmentOut;
        return (await stock.ApplyAsync(c.ProductId, c.WarehouseId, type, Math.Abs(diff), null, "Adjustment", null, c.Reason, ct)).Id;
    }
}

internal sealed class ReserveStockHandler(IInventoryDbContext db) : ICommandHandler<ReserveStockCommand, decimal>
{
    public async Task<decimal> Handle(ReserveStockCommand c, CancellationToken ct)
    {
        var level = await db.StockLevels.FirstOrDefaultAsync(l => l.ProductId == c.ProductId && l.WarehouseId == c.WarehouseId, ct)
                    ?? throw new NotFoundException(nameof(StockLevel), $"{c.ProductId}/{c.WarehouseId}");
        if (c.Release) level.Release(c.Quantity); else level.Reserve(c.Quantity);
        return level.Available;
    }
}

public sealed record StockLevelDto(Guid ProductId, string Sku, string ProductName, Guid WarehouseId, string WarehouseCode,
    decimal OnHand, decimal Reserved, decimal Available, decimal AverageCost, decimal Value);
public sealed record GetStockLevelsQuery(Guid? ProductId, Guid? WarehouseId, bool LowStockOnly = false) : IQuery<List<StockLevelDto>>;

internal sealed class GetStockLevelsHandler(IInventoryDbContext db) : IQueryHandler<GetStockLevelsQuery, List<StockLevelDto>>
{
    public Task<List<StockLevelDto>> Handle(GetStockLevelsQuery q, CancellationToken ct)
    {
        var query = from l in db.StockLevels.AsNoTracking()
                    join p in db.Products on l.ProductId equals p.Id
                    join w in db.Warehouses on l.WarehouseId equals w.Id
                    where (q.ProductId == null || l.ProductId == q.ProductId)
                          && (q.WarehouseId == null || l.WarehouseId == q.WarehouseId)
                          && (!q.LowStockOnly || (p.ReorderLevel > 0 && l.OnHand - l.Reserved <= p.ReorderLevel))
                    orderby p.Sku, w.Code
                    select new StockLevelDto(p.Id, p.Sku, p.Name, w.Id, w.Code, l.OnHand, l.Reserved, l.OnHand - l.Reserved, l.AverageCost, l.OnHand * l.AverageCost);
        return query.ToListAsync(ct);
    }
}

public sealed record MovementDto(Guid Id, Guid ProductId, Guid WarehouseId, string Type, decimal Quantity, decimal UnitCost, decimal BalanceAfter,
    string? ReferenceType, Guid? ReferenceId, string? Note, DateTime OccurredAtUtc);
public sealed record GetMovementsQuery(Guid ProductId, Guid? WarehouseId, DateTime? From, DateTime? To, int Page, int PageSize) : IQuery<PagedResult<MovementDto>>;

internal sealed class GetMovementsHandler(IInventoryDbContext db) : IQueryHandler<GetMovementsQuery, PagedResult<MovementDto>>
{
    public async Task<PagedResult<MovementDto>> Handle(GetMovementsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 1, 500);
        var query = db.StockMovements.AsNoTracking().Where(m => m.ProductId == q.ProductId);
        if (q.WarehouseId != null) query = query.Where(m => m.WarehouseId == q.WarehouseId);
        if (q.From != null) query = query.Where(m => m.OccurredAtUtc >= q.From);
        if (q.To != null) query = query.Where(m => m.OccurredAtUtc <= q.To);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.OccurredAtUtc).Skip((page - 1) * size).Take(size)
            .Select(m => new MovementDto(m.Id, m.ProductId, m.WarehouseId, m.Type.ToString(), m.Quantity, m.UnitCost, m.BalanceAfter, m.ReferenceType, m.ReferenceId, m.Note, m.OccurredAtUtc))
            .ToListAsync(ct);
        return new PagedResult<MovementDto>(items, page, size, total);
    }
}

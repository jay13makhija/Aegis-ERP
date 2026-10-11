namespace Erp.Procurement.Application;

public sealed record PoLineInput(Guid ProductId, string Sku, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRate);
public sealed record CreatePurchaseOrderCommand(Guid SupplierId, Guid WarehouseId, string? Currency, DateTime? ExpectedDateUtc, List<PoLineInput> Lines) : ICommand<Guid>;
public sealed record SubmitPurchaseOrderCommand(Guid Id) : ICommand<Unit>;
public sealed record CancelPurchaseOrderCommand(Guid Id) : ICommand<Unit>;
public sealed record ReceiveLineInput(Guid LineId, decimal Quantity);
public sealed record ReceiveGoodsCommand(Guid Id, List<ReceiveLineInput> Items) : ICommand<string>;

public sealed record PoLineDto(Guid Id, Guid ProductId, string Sku, string Description, decimal Quantity, decimal ReceivedQuantity, decimal UnitPrice, decimal TaxRate);
public sealed record PurchaseOrderDto(Guid Id, string Number, Guid SupplierId, Guid WarehouseId, string Currency, string Status, DateTime OrderDateUtc,
    decimal Subtotal, decimal TaxTotal, decimal Total, string RequestedBy, string? DecidedBy, string? DecisionNote, List<PoLineDto> Lines);
public sealed record PurchaseOrderSummary(Guid Id, string Number, Guid SupplierId, string Status, string Currency, decimal Total, DateTime OrderDateUtc);
public sealed record GetPurchaseOrderQuery(Guid Id) : IQuery<PurchaseOrderDto>;
public sealed record ListPurchaseOrdersQuery(PurchaseOrderStatus? Status, int Page, int PageSize) : IQuery<PagedResult<PurchaseOrderSummary>>;

internal sealed class CreatePurchaseOrderHandler(IProcurementDbContext db, ICurrentUser me) : ICommandHandler<CreatePurchaseOrderCommand, Guid>
{
    public async Task<Guid> Handle(CreatePurchaseOrderCommand c, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == c.SupplierId, ct) ?? throw new NotFoundException(nameof(Supplier), c.SupplierId);
        if (!supplier.IsActive) throw new DomainException("Supplier is inactive.");
        if (c.Lines is not { Count: > 0 }) throw new DomainException("At least one line is required.");
        var number = $"PO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var po = new PurchaseOrder(number, supplier.Id, c.WarehouseId, c.Currency ?? supplier.Currency, c.ExpectedDateUtc, me.UserName);
        foreach (var l in c.Lines) po.AddLine(l.ProductId, l.Sku, l.Description, l.Quantity, l.UnitPrice, l.TaxRate);
        db.PurchaseOrders.Add(po);
        return po.Id;
    }
}

internal sealed class SubmitPurchaseOrderHandler(IProcurementDbContext db) : ICommandHandler<SubmitPurchaseOrderCommand, Unit>
{
    public async Task<Unit> Handle(SubmitPurchaseOrderCommand c, CancellationToken ct)
    {
        (await Load(db, c.Id, ct)).Submit();    // raises PurchaseOrderSubmittedEvent → Workflow service decides who must approve
        return Unit.Value;
    }
    internal static async Task<PurchaseOrder> Load(IProcurementDbContext db, Guid id, CancellationToken ct) =>
        await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException(nameof(PurchaseOrder), id);
}

internal sealed class CancelPurchaseOrderHandler(IProcurementDbContext db) : ICommandHandler<CancelPurchaseOrderCommand, Unit>
{
    public async Task<Unit> Handle(CancelPurchaseOrderCommand c, CancellationToken ct)
    { (await SubmitPurchaseOrderHandler.Load(db, c.Id, ct)).Cancel(); return Unit.Value; }
}

internal sealed class ReceiveGoodsHandler(IProcurementDbContext db) : ICommandHandler<ReceiveGoodsCommand, string>
{
    public async Task<string> Handle(ReceiveGoodsCommand c, CancellationToken ct)
    {
        var po = await SubmitPurchaseOrderHandler.Load(db, c.Id, ct);
        po.Receive(c.Items.Select(i => (i.LineId, i.Quantity)).ToList());   // raises GoodsReceivedEvent → Inventory books the stock
        return po.Status.ToString();
    }
}

internal sealed class GetPurchaseOrderHandler(IProcurementDbContext db) : IQueryHandler<GetPurchaseOrderQuery, PurchaseOrderDto>
{
    public async Task<PurchaseOrderDto> Handle(GetPurchaseOrderQuery q, CancellationToken ct)
    {
        var po = await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == q.Id, ct) ?? throw new NotFoundException(nameof(PurchaseOrder), q.Id);
        return new PurchaseOrderDto(po.Id, po.Number, po.SupplierId, po.WarehouseId, po.Currency, po.Status.ToString(), po.OrderDateUtc, po.Subtotal, po.TaxTotal, po.Total,
            po.RequestedBy, po.DecidedBy, po.DecisionNote,
            po.Lines.Select(l => new PoLineDto(l.Id, l.ProductId, l.Sku, l.Description, l.Quantity, l.ReceivedQuantity, l.UnitPrice, l.TaxRate)).ToList());
    }
}

internal sealed class ListPurchaseOrdersHandler(IProcurementDbContext db) : IQueryHandler<ListPurchaseOrdersQuery, PagedResult<PurchaseOrderSummary>>
{
    public async Task<PagedResult<PurchaseOrderSummary>> Handle(ListPurchaseOrdersQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 1, 200);
        var query = db.PurchaseOrders.AsNoTracking().AsQueryable();
        if (q.Status != null) query = query.Where(p => p.Status == q.Status);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(p => p.OrderDateUtc).Skip((page - 1) * size).Take(size)
            .Select(p => new PurchaseOrderSummary(p.Id, p.Number, p.SupplierId, p.Status.ToString(), p.Currency, p.Total, p.OrderDateUtc)).ToListAsync(ct);
        return new PagedResult<PurchaseOrderSummary>(items, page, size, total);
    }
}

/// <summary>Workflow → Procurement: approval outcome decides the PO state. Idempotent: ignores orders that are no longer pending.</summary>
internal sealed class WorkflowCompletedHandler(IProcurementDbContext db) : IIntegrationEventHandler<WorkflowCompletedEvent>
{
    public async Task Handle(WorkflowCompletedEvent e, CancellationToken ct)
    {
        if (e.EntityType != "PurchaseOrder") return;
        var po = await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == e.EntityId, ct);
        if (po is null || po.Status != PurchaseOrderStatus.PendingApproval) return;
        if (e.Outcome == "Approved") po.Approve(e.DecidedBy, e.Comment); else po.Reject(e.DecidedBy, e.Comment);
    }
}

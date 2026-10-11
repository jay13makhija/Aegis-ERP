namespace Erp.Inventory.Application;

/// <summary>Procurement → Inventory: goods received against a PO become stock receipts (idempotent via the inbox).</summary>
internal sealed class GoodsReceivedHandler(IStockService stock) : IIntegrationEventHandler<GoodsReceivedEvent>
{
    public async Task Handle(GoodsReceivedEvent e, CancellationToken ct)
    {
        foreach (var line in e.Lines)
            await stock.ApplyAsync(line.ProductId, e.WarehouseId, MovementType.Receipt, line.Quantity, line.UnitCost,
                "PurchaseOrder", e.PurchaseOrderId, e.PurchaseOrderNumber, ct);
    }
}

/// <summary>Fans committed ledger movements out to live dashboards.</summary>
internal sealed class StockMovedBroadcastHandler(IStockNotifier notifier) : IIntegrationEventHandler<StockMovedEvent>
{
    public Task Handle(StockMovedEvent e, CancellationToken ct) => notifier.NotifyAsync(e, ct);
}

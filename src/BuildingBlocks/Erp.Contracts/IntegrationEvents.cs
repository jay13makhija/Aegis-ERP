namespace Erp.Contracts;

public sealed record AuditRecordedEvent(string Service, string EntityType, string EntityId, string Action,
    string? ChangesJson, Guid? UserId, string? UserName, string? ClientIp) : IntegrationEvent;

public sealed record TenantRegisteredEvent(Guid NewTenantId, string Code, string Name) : IntegrationEvent;

public sealed record PurchaseOrderSubmittedEvent(Guid PurchaseOrderId, string Number, Guid SupplierId, string Currency,
    decimal TotalAmount, string RequestedBy) : IntegrationEvent;
public sealed record PurchaseOrderApprovedEvent(Guid PurchaseOrderId, string Number, string Currency, decimal TotalAmount, string ApprovedBy) : IntegrationEvent;
public sealed record PurchaseOrderRejectedEvent(Guid PurchaseOrderId, string Number, string RejectedBy, string? Reason) : IntegrationEvent;

public sealed record GoodsReceivedLine(Guid ProductId, string Sku, decimal Quantity, decimal UnitCost);
public sealed record GoodsReceivedEvent(Guid PurchaseOrderId, string PurchaseOrderNumber, Guid WarehouseId,
    IReadOnlyList<GoodsReceivedLine> Lines) : IntegrationEvent;

public sealed record WorkflowCompletedEvent(Guid InstanceId, string EntityType, Guid EntityId, string Outcome,
    string DecidedBy, string? Comment) : IntegrationEvent;

public sealed record StockMovedEvent(Guid ProductId, string Sku, Guid WarehouseId, string MovementType,
    decimal Quantity, decimal UnitCost, decimal BalanceAfter) : IntegrationEvent;
public sealed record LowStockEvent(Guid ProductId, string Sku, Guid WarehouseId, decimal Available, decimal ReorderLevel) : IntegrationEvent;

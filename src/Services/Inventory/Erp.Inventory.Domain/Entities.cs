namespace Erp.Inventory.Domain;

public sealed class Warehouse : TenantEntity
{
    private Warehouse() { }
    public Warehouse(string code, string name, string? address) { Code = Guard.NotBlank(code, nameof(code)).ToUpperInvariant(); Name = Guard.NotBlank(name, nameof(name)); Address = address; IsActive = true; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Address { get; private set; }
    public bool IsActive { get; private set; }
}

public sealed class Product : TenantEntity
{
    private Product() { }
    public Product(string sku, string name, string uom, decimal standardCost, string currency, decimal reorderLevel, string? category, string? description)
    {
        Sku = Guard.NotBlank(sku, nameof(sku)).ToUpperInvariant(); Name = Guard.NotBlank(name, nameof(name));
        UnitOfMeasure = Guard.NotBlank(uom, nameof(uom)); StandardCost = Guard.NotNegative(standardCost, nameof(standardCost));
        Currency = Guard.NotBlank(currency, nameof(currency)).ToUpperInvariant(); ReorderLevel = Guard.NotNegative(reorderLevel, nameof(reorderLevel));
        Category = category; Description = description; IsActive = true;
    }
    public string Sku { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public string? Category { get; private set; }
    public string UnitOfMeasure { get; private set; } = "EA";
    public decimal StandardCost { get; private set; }
    public string Currency { get; private set; } = "USD";
    public decimal ReorderLevel { get; private set; }
    public bool IsActive { get; private set; }
}

/// <summary>Current balance of one product in one warehouse. Concurrent updates are rejected through the row-version token.</summary>
public sealed class StockLevel : TenantEntity
{
    private StockLevel() { }
    public StockLevel(Guid productId, Guid warehouseId) { ProductId = productId; WarehouseId = warehouseId; }
    public Guid ProductId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public decimal OnHand { get; private set; }
    public decimal Reserved { get; private set; }
    public decimal AverageCost { get; private set; }
    public decimal Available => OnHand - Reserved;

    public void Receive(decimal qty, decimal unitCost)
    {
        Guard.Positive(qty, nameof(qty)); Guard.NotNegative(unitCost, nameof(unitCost));
        var value = OnHand * AverageCost + qty * unitCost;   // moving weighted-average costing
        OnHand += qty;
        AverageCost = Math.Round(value / OnHand, 4);
    }
    public void Issue(decimal qty)
    {
        Guard.Positive(qty, nameof(qty));
        if (qty > Available) throw new DomainException($"Insufficient stock: requested {qty}, available {Available}.");
        OnHand -= qty;
    }
    public void Reserve(decimal qty)
    {
        Guard.Positive(qty, nameof(qty));
        if (qty > Available) throw new DomainException($"Cannot reserve {qty}; only {Available} available.");
        Reserved += qty;
    }
    public void Release(decimal qty)
    {
        Guard.Positive(qty, nameof(qty));
        if (qty > Reserved) throw new DomainException($"Cannot release {qty}; only {Reserved} reserved.");
        Reserved -= qty;
    }
    public void CheckReorder(Product product)
    {
        if (product.ReorderLevel > 0 && Available <= product.ReorderLevel)
            Raise(new LowStockEvent(ProductId, product.Sku, WarehouseId, Available, product.ReorderLevel));
    }
}

public enum MovementType { Receipt, Issue, TransferOut, TransferIn, AdjustmentIn, AdjustmentOut }

/// <summary>Append-only stock ledger. Every balance change has exactly one movement.</summary>
public sealed class StockMovement : TenantEntity
{
    private StockMovement() { }
    public StockMovement(Guid productId, string sku, Guid warehouseId, MovementType type, decimal quantity, decimal unitCost,
        decimal balanceAfter, string? referenceType, Guid? referenceId, string? note)
    {
        ProductId = productId; WarehouseId = warehouseId; Type = type; Quantity = quantity; UnitCost = unitCost; BalanceAfter = balanceAfter;
        ReferenceType = referenceType; ReferenceId = referenceId; Note = note; OccurredAtUtc = DateTime.UtcNow;
        Raise(new StockMovedEvent(productId, sku, warehouseId, type.ToString(), quantity, unitCost, balanceAfter));
    }
    public Guid ProductId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public MovementType Type { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitCost { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string? ReferenceType { get; private set; }
    public Guid? ReferenceId { get; private set; }
    public string? Note { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
}

public enum ShipmentStatus { Planned, PickedUp, InTransit, OutForDelivery, Delivered, Exception, Cancelled }

public sealed class ShipmentEvent
{
    private ShipmentEvent() { }
    public ShipmentEvent(ShipmentStatus status, string? location, string? note) { Status = status; Location = location; Note = note; AtUtc = DateTime.UtcNow; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public ShipmentStatus Status { get; private set; }
    public string? Location { get; private set; }
    public string? Note { get; private set; }
    public DateTime AtUtc { get; private set; }
}

public sealed class Shipment : TenantEntity
{
    private readonly List<ShipmentEvent> _history = [];
    private Shipment() { }
    public Shipment(string carrier, string trackingNumber, Guid fromWarehouseId, string destination, DateTime? expectedDeliveryUtc)
    {
        Carrier = Guard.NotBlank(carrier, nameof(carrier)); TrackingNumber = Guard.NotBlank(trackingNumber, nameof(trackingNumber));
        FromWarehouseId = fromWarehouseId; Destination = Guard.NotBlank(destination, nameof(destination)); ExpectedDeliveryUtc = expectedDeliveryUtc;
        _history.Add(new ShipmentEvent(ShipmentStatus.Planned, null, "Shipment created"));
    }
    public string Carrier { get; private set; } = "";
    public string TrackingNumber { get; private set; } = "";
    public Guid FromWarehouseId { get; private set; }
    public string Destination { get; private set; } = "";
    public DateTime? ExpectedDeliveryUtc { get; private set; }
    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Planned;
    public IReadOnlyCollection<ShipmentEvent> History => _history;

    public void UpdateStatus(ShipmentStatus status, string? location, string? note)
    {
        if (Status is ShipmentStatus.Delivered or ShipmentStatus.Cancelled) throw new DomainException($"Shipment is already {Status}.");
        Status = status; _history.Add(new ShipmentEvent(status, location, note));
    }
}

namespace Erp.Procurement.Domain;

public sealed class Supplier : TenantEntity
{
    private Supplier() { }
    public Supplier(string code, string name, string? email, string? phone, string countryCode, string currency, int paymentTermsDays)
    {
        Code = Guard.NotBlank(code, nameof(code)).ToUpperInvariant(); Name = Guard.NotBlank(name, nameof(name));
        Email = email; Phone = phone; CountryCode = Guard.NotBlank(countryCode, nameof(countryCode)).ToUpperInvariant();
        Currency = Guard.NotBlank(currency, nameof(currency)).ToUpperInvariant(); PaymentTermsDays = paymentTermsDays; IsActive = true;
    }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string CountryCode { get; private set; } = "";
    public string Currency { get; private set; } = "";
    public int PaymentTermsDays { get; private set; }
    public bool IsActive { get; private set; }
}

public enum PurchaseOrderStatus { Draft, PendingApproval, Approved, Rejected, PartiallyReceived, Received, Cancelled }

public sealed class PurchaseOrderLine
{
    private PurchaseOrderLine() { }
    public PurchaseOrderLine(Guid productId, string sku, string description, decimal quantity, decimal unitPrice, decimal taxRate)
    {
        ProductId = productId; Sku = Guard.NotBlank(sku, nameof(sku)); Description = description;
        Quantity = Guard.Positive(quantity, nameof(quantity)); UnitPrice = Guard.NotNegative(unitPrice, nameof(unitPrice));
        TaxRate = taxRate is < 0 or > 1 ? throw new DomainException("taxRate must be a fraction between 0 and 1.") : taxRate;
    }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = "";
    public string Description { get; private set; } = "";
    public decimal Quantity { get; private set; }
    public decimal ReceivedQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal TaxRate { get; private set; }
    public decimal Net => Math.Round(Quantity * UnitPrice, 4);
    public decimal Tax => Math.Round(Net * TaxRate, 4);
    public decimal Remaining => Quantity - ReceivedQuantity;
    internal void Receive(decimal qty)
    {
        Guard.Positive(qty, nameof(qty));
        if (qty > Remaining) throw new DomainException($"Line {Sku}: receiving {qty} exceeds outstanding {Remaining}.");
        ReceivedQuantity += qty;
    }
}

public sealed class PurchaseOrder : TenantEntity
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private PurchaseOrder() { }
    public PurchaseOrder(string number, Guid supplierId, Guid warehouseId, string currency, DateTime? expectedDateUtc, string requestedBy)
    {
        Number = number; SupplierId = supplierId; WarehouseId = warehouseId; Currency = Guard.NotBlank(currency, nameof(currency)).ToUpperInvariant();
        ExpectedDateUtc = expectedDateUtc; RequestedBy = requestedBy; OrderDateUtc = DateTime.UtcNow; Status = PurchaseOrderStatus.Draft;
    }
    public string Number { get; private set; } = "";
    public Guid SupplierId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public string Currency { get; private set; } = "";
    public PurchaseOrderStatus Status { get; private set; }
    public DateTime OrderDateUtc { get; private set; }
    public DateTime? ExpectedDateUtc { get; private set; }
    public string RequestedBy { get; private set; } = "";
    public string? DecidedBy { get; private set; }
    public string? DecisionNote { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal TaxTotal { get; private set; }
    public decimal Total { get; private set; }
    public IReadOnlyCollection<PurchaseOrderLine> Lines => _lines;

    public void AddLine(Guid productId, string sku, string description, decimal qty, decimal unitPrice, decimal taxRate)
    {
        Require(PurchaseOrderStatus.Draft);
        _lines.Add(new PurchaseOrderLine(productId, sku, description, qty, unitPrice, taxRate));
        Subtotal = _lines.Sum(l => l.Net); TaxTotal = _lines.Sum(l => l.Tax); Total = Subtotal + TaxTotal;
    }

    public void Submit()
    {
        Require(PurchaseOrderStatus.Draft);
        if (_lines.Count == 0) throw new DomainException("A purchase order needs at least one line.");
        Status = PurchaseOrderStatus.PendingApproval;
        Raise(new PurchaseOrderSubmittedEvent(Id, Number, SupplierId, Currency, Total, RequestedBy));
    }

    public void Approve(string by, string? note)
    {
        Require(PurchaseOrderStatus.PendingApproval);
        Status = PurchaseOrderStatus.Approved; DecidedBy = by; DecisionNote = note;
        Raise(new PurchaseOrderApprovedEvent(Id, Number, Currency, Total, by));
    }

    public void Reject(string by, string? reason)
    {
        Require(PurchaseOrderStatus.PendingApproval);
        Status = PurchaseOrderStatus.Rejected; DecidedBy = by; DecisionNote = reason;
        Raise(new PurchaseOrderRejectedEvent(Id, Number, by, reason));
    }

    public void Receive(IReadOnlyCollection<(Guid LineId, decimal Quantity)> items)
    {
        if (Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
            throw new DomainException($"Goods can only be received against approved orders (current status: {Status}).");
        if (items.Count == 0) throw new DomainException("Nothing to receive.");
        var received = new List<GoodsReceivedLine>();
        foreach (var (lineId, qty) in items)
        {
            var line = _lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException(nameof(PurchaseOrderLine), lineId);
            line.Receive(qty);
            received.Add(new GoodsReceivedLine(line.ProductId, line.Sku, qty, line.UnitPrice));
        }
        Status = _lines.All(l => l.Remaining == 0) ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        Raise(new GoodsReceivedEvent(Id, Number, WarehouseId, received));
    }

    public void Cancel()
    {
        if (Status is PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled)
            throw new DomainException($"Cannot cancel an order that is {Status}.");
        Status = PurchaseOrderStatus.Cancelled;
    }

    private void Require(PurchaseOrderStatus expected)
    { if (Status != expected) throw new DomainException($"Operation requires status {expected}, but the order is {Status}."); }
}

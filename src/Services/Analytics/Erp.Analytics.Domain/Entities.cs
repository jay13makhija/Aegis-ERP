namespace Erp.Analytics.Domain;

public static class Kpi
{
    public const string StockReceivedQty = "stock.received.qty";
    public const string StockIssuedQty = "stock.issued.qty";
    public const string PoApprovedCount = "procurement.po.approved.count";
    public const string PoApprovedValue = "procurement.po.approved.value";   // dimension = currency
    public const string PoRejectedCount = "procurement.po.rejected.count";
    public const string LowStockAlerts = "inventory.lowstock.alerts";
    public static readonly string[] All = [StockReceivedQty, StockIssuedQty, PoApprovedCount, PoApprovedValue, PoRejectedCount, LowStockAlerts];
}

/// <summary>Read model fed by integration events. One row per tenant/day/metric/dimension (dimension = SKU, currency, ...).</summary>
public sealed class DailyMetric : TenantEntity
{
    private DailyMetric() { }
    public DailyMetric(DateOnly date, string code, string dimension, decimal value) { Date = date; Code = code; Dimension = dimension; Value = value; }
    public DateOnly Date { get; private set; }
    public string Code { get; private set; } = "";
    public string Dimension { get; private set; } = "";
    public decimal Value { get; private set; }
    public void Add(decimal v) => Value += v;
}

public sealed class DashboardDefinition : TenantEntity
{
    private DashboardDefinition() { }
    public DashboardDefinition(string role, string name, IEnumerable<string> kpiCodes)
    {
        Role = Guard.NotBlank(role, nameof(role)); Name = Guard.NotBlank(name, nameof(name)); KpiCodes = kpiCodes.Distinct().ToList();
        var bad = KpiCodes.Where(k => !Kpi.All.Contains(k)).ToList();
        if (bad.Count > 0) throw new DomainException($"Unknown KPI codes: {string.Join(", ", bad)}");
    }
    public string Role { get; private set; } = "";
    public string Name { get; private set; } = "";
    public List<string> KpiCodes { get; private set; } = [];
}

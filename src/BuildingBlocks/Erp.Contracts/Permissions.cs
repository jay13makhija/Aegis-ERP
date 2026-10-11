using System.Reflection;
namespace Erp.Contracts;

/// <summary>Single catalogue of permissions across all modules. "*" (wildcard) is reserved for tenant administrators.</summary>
public static class Perm
{
    public const string UsersRead = "identity.users.read";
    public const string UsersWrite = "identity.users.write";
    public const string RolesRead = "identity.roles.read";
    public const string RolesWrite = "identity.roles.write";
    public const string ProductsRead = "inventory.products.read";
    public const string ProductsWrite = "inventory.products.write";
    public const string StockRead = "inventory.stock.read";
    public const string StockWrite = "inventory.stock.write";
    public const string SuppliersRead = "procurement.suppliers.read";
    public const string SuppliersWrite = "procurement.suppliers.write";
    public const string PoRead = "procurement.po.read";
    public const string PoWrite = "procurement.po.write";
    public const string PoReceive = "procurement.po.receive";
    public const string LocalizationRead = "localization.read";
    public const string LocalizationWrite = "localization.write";
    public const string WorkflowRead = "workflow.read";
    public const string WorkflowWrite = "workflow.write";
    public const string WorkflowDecide = "workflow.decide";
    public const string AuditRead = "audit.read";
    public const string AnalyticsRead = "analytics.read";
    public const string AnalyticsWrite = "analytics.write";

    public static IReadOnlyList<string> All { get; } = typeof(Perm)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).OrderBy(x => x).ToList();
}

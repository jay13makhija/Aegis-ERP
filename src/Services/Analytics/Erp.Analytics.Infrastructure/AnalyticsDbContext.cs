namespace Erp.Analytics.Infrastructure;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IAnalyticsDbContext
{
    public DbSet<DailyMetric> DailyMetrics => Set<DailyMetric>();
    public DbSet<DashboardDefinition> Dashboards => Set<DashboardDefinition>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<DailyMetric>(b => { b.ToTable("DailyMetrics"); b.HasIndex(x => new { x.TenantId, x.Code, x.Date, x.Dimension }).IsUnique(); });
        mb.Entity<DashboardDefinition>(b => { b.ToTable("Dashboards"); b.HasIndex(x => new { x.TenantId, x.Role, x.Name }); });
    }
}

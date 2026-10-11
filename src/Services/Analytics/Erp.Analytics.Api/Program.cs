var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<AnalyticsDbContext>("analytics", typeof(IAnalyticsDbContext).Assembly);
builder.Services.AddScoped<IAnalyticsDbContext>(sp => sp.GetRequiredService<AnalyticsDbContext>());
builder.Services.AddScoped<MetricRecorder>();

var app = builder.Build();
await app.InitializeDatabaseAsync<AnalyticsDbContext>();
app.UseErpService();

var g = app.MapGroup("/api/analytics").WithTags("Analytics");
g.MapGet("/kpis", () => Kpi.All).RequirePermission(Perm.AnalyticsRead);
g.MapGet("/series", (string code, string? dimension, DateOnly? from, DateOnly? to, IDispatcher d, CancellationToken ct) =>
{
    var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return d.Query(new KpiSeriesQuery(code, dimension, from ?? end.AddDays(-29), end), ct);
}).RequirePermission(Perm.AnalyticsRead);
g.MapGet("/forecast", (string code, string? dimension, int? historyDays, int? horizonDays, double? alpha, IDispatcher d, CancellationToken ct) =>
    d.Query(new ForecastQuery(code, dimension, historyDays ?? 90, horizonDays ?? 14, alpha ?? 0.3), ct)).RequirePermission(Perm.AnalyticsRead);
g.MapGet("/dashboards/mine", (IDispatcher d, CancellationToken ct) => d.Query(new MyDashboardsQuery(), ct)).RequirePermission(Perm.AnalyticsRead);
g.MapPut("/dashboards", async (UpsertDashboardCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.AnalyticsWrite);
app.Run();

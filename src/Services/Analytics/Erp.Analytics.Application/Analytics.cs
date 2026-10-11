namespace Erp.Analytics.Application;

public interface IAnalyticsDbContext : IUnitOfWork
{
    DbSet<DailyMetric> DailyMetrics { get; }
    DbSet<DashboardDefinition> Dashboards { get; }
}

public sealed class MetricRecorder(IAnalyticsDbContext db)
{
    public async Task AddAsync(DateTime whenUtc, string code, string dimension, decimal value, CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(whenUtc);
        var row = db.DailyMetrics.Local.FirstOrDefault(m => m.Date == date && m.Code == code && m.Dimension == dimension)
                  ?? await db.DailyMetrics.FirstOrDefaultAsync(m => m.Date == date && m.Code == code && m.Dimension == dimension, ct);
        if (row is null) db.DailyMetrics.Add(new DailyMetric(date, code, dimension, value)); else row.Add(value);
    }
}

// ---- ingestion: event -> KPI ----
internal sealed class StockMovedMetrics(MetricRecorder rec) : IIntegrationEventHandler<StockMovedEvent>
{
    public Task Handle(StockMovedEvent e, CancellationToken ct) => e.MovementType switch
    {
        "Receipt" => rec.AddAsync(e.OccurredAtUtc, Kpi.StockReceivedQty, e.Sku, e.Quantity, ct),
        "Issue" => rec.AddAsync(e.OccurredAtUtc, Kpi.StockIssuedQty, e.Sku, e.Quantity, ct),
        _ => Task.CompletedTask   // transfers/adjustments do not change demand or supply KPIs
    };
}
internal sealed class PoApprovedMetrics(MetricRecorder rec) : IIntegrationEventHandler<PurchaseOrderApprovedEvent>
{
    public async Task Handle(PurchaseOrderApprovedEvent e, CancellationToken ct)
    {
        await rec.AddAsync(e.OccurredAtUtc, Kpi.PoApprovedCount, e.Currency, 1, ct);
        await rec.AddAsync(e.OccurredAtUtc, Kpi.PoApprovedValue, e.Currency, e.TotalAmount, ct);
    }
}
internal sealed class PoRejectedMetrics(MetricRecorder rec) : IIntegrationEventHandler<PurchaseOrderRejectedEvent>
{
    public Task Handle(PurchaseOrderRejectedEvent e, CancellationToken ct) => rec.AddAsync(e.OccurredAtUtc, Kpi.PoRejectedCount, "all", 1, ct);
}
internal sealed class LowStockMetrics(MetricRecorder rec) : IIntegrationEventHandler<LowStockEvent>
{
    public Task Handle(LowStockEvent e, CancellationToken ct) => rec.AddAsync(e.OccurredAtUtc, Kpi.LowStockAlerts, e.Sku, 1, ct);
}

// ---- series ----
public sealed record SeriesPoint(DateOnly Date, decimal Value);
public sealed record KpiSeriesQuery(string Code, string? Dimension, DateOnly From, DateOnly To) : IQuery<List<SeriesPoint>>;

internal sealed class KpiSeriesHandler(IAnalyticsDbContext db) : IQueryHandler<KpiSeriesQuery, List<SeriesPoint>>
{
    public async Task<List<SeriesPoint>> Handle(KpiSeriesQuery q, CancellationToken ct)
    {
        var query = db.DailyMetrics.AsNoTracking().Where(m => m.Code == q.Code && m.Date >= q.From && m.Date <= q.To);
        if (q.Dimension != null) query = query.Where(m => m.Dimension == q.Dimension);
        var rows = await query.GroupBy(m => m.Date).Select(g => new { Date = g.Key, Value = g.Sum(x => x.Value) }).ToListAsync(ct);
        var map = rows.ToDictionary(r => r.Date, r => r.Value);
        var result = new List<SeriesPoint>();
        for (var d = q.From; d <= q.To; d = d.AddDays(1)) result.Add(new SeriesPoint(d, map.GetValueOrDefault(d)));   // zero-filled daily series
        return result;
    }
}

// ---- forecast ----
public sealed record ForecastResult(string Code, string? Dimension, string Method, double Alpha, int HistoryDays, int NonZeroDays,
    decimal? InSampleMae, List<SeriesPoint> Forecast, string? Warning);
public sealed record ForecastQuery(string Code, string? Dimension, int HistoryDays, int HorizonDays, double Alpha) : IQuery<ForecastResult>;

/// <summary>Simple exponential smoothing on the zero-filled daily series. Reports in-sample one-step MAE so the caller can judge it. Needs real history to be meaningful.</summary>
public static class Smoothing
{
    public static (decimal Level, decimal? Mae) Fit(IReadOnlyList<decimal> series, double alpha)
    {
        var a = (decimal)alpha; var level = series[0]; decimal absErr = 0; var n = 0;
        for (var i = 1; i < series.Count; i++)
        {
            absErr += Math.Abs(series[i] - level); n++;      // forecast for day i was the level after day i-1
            level = a * series[i] + (1 - a) * level;
        }
        return (level, n == 0 ? null : Math.Round(absErr / n, 4));
    }
}

internal sealed class ForecastHandler(IAnalyticsDbContext db) : IQueryHandler<ForecastQuery, ForecastResult>
{
    public async Task<ForecastResult> Handle(ForecastQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.HistoryDays, 14, 730); var horizon = Math.Clamp(q.HorizonDays, 1, 90);
        var alpha = Math.Clamp(q.Alpha, 0.05, 0.95);
        var to = DateOnly.FromDateTime(DateTime.UtcNow); var from = to.AddDays(-(days - 1));
        var query = db.DailyMetrics.AsNoTracking().Where(m => m.Code == q.Code && m.Date >= from && m.Date <= to);
        if (q.Dimension != null) query = query.Where(m => m.Dimension == q.Dimension);
        var rows = (await query.GroupBy(m => m.Date).Select(g => new { Date = g.Key, Value = g.Sum(x => x.Value) }).ToListAsync(ct)).ToDictionary(r => r.Date, r => r.Value);
        var series = Enumerable.Range(0, days).Select(i => rows.GetValueOrDefault(from.AddDays(i))).ToList();
        var nonZero = series.Count(v => v != 0);
        var (level, mae) = Smoothing.Fit(series, alpha);
        var forecast = Enumerable.Range(1, horizon).Select(i => new SeriesPoint(to.AddDays(i), Math.Round(level, 4))).ToList();
        string? warning = nonZero < 14 ? $"Only {nonZero} days with data in the window; treat this forecast as indicative, not reliable." : null;
        return new ForecastResult(q.Code, q.Dimension, "simple-exponential-smoothing", alpha, days, nonZero, mae, forecast, warning);
    }
}

// ---- role-based dashboards ----
public sealed record UpsertDashboardCommand(string Role, string Name, List<string> KpiCodes) : ICommand<Guid>;
public sealed record KpiCard(string Code, decimal Last30Days, decimal Previous30Days, decimal? ChangePercent, List<SeriesPoint> Sparkline);
public sealed record DashboardDto(string Name, string Role, List<KpiCard> Kpis);
public sealed record MyDashboardsQuery() : IQuery<List<DashboardDto>>;

internal sealed class UpsertDashboardHandler(IAnalyticsDbContext db) : ICommandHandler<UpsertDashboardCommand, Guid>
{
    public async Task<Guid> Handle(UpsertDashboardCommand c, CancellationToken ct)
    {
        foreach (var old in await db.Dashboards.Where(d => d.Role == c.Role && d.Name == c.Name).ToListAsync(ct)) db.Dashboards.Remove(old);   // soft-deleted
        var d = new DashboardDefinition(c.Role, c.Name, c.KpiCodes);
        db.Dashboards.Add(d);
        return d.Id;
    }
}

internal sealed class MyDashboardsHandler(IAnalyticsDbContext db, ICurrentUser me) : IQueryHandler<MyDashboardsQuery, List<DashboardDto>>
{
    public async Task<List<DashboardDto>> Handle(MyDashboardsQuery q, CancellationToken ct)
    {
        var roles = me.Roles.ToList();
        var defs = await db.Dashboards.AsNoTracking().Where(d => roles.Contains(d.Role)).ToListAsync(ct);
        if (defs.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow); var from = today.AddDays(-59);
        var codes = defs.SelectMany(d => d.KpiCodes).Distinct().ToList();
        var rows = await db.DailyMetrics.AsNoTracking().Where(m => codes.Contains(m.Code) && m.Date >= from && m.Date <= today)
            .GroupBy(m => new { m.Code, m.Date }).Select(g => new { g.Key.Code, g.Key.Date, Value = g.Sum(x => x.Value) }).ToListAsync(ct);

        KpiCard Card(string code)
        {
            var mine = rows.Where(r => r.Code == code).ToDictionary(r => r.Date, r => r.Value);
            decimal Sum(DateOnly a, DateOnly b) => mine.Where(kv => kv.Key >= a && kv.Key <= b).Sum(kv => kv.Value);
            var last = Sum(today.AddDays(-29), today); var prev = Sum(today.AddDays(-59), today.AddDays(-30));
            var spark = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d => new SeriesPoint(d, mine.GetValueOrDefault(d))).ToList();
            return new KpiCard(code, last, prev, prev == 0 ? null : Math.Round((last - prev) / prev * 100, 1), spark);
        }
        return defs.Select(d => new DashboardDto(d.Name, d.Role, d.KpiCodes.Select(Card).ToList())).ToList();
    }
}

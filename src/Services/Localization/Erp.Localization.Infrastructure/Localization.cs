using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Erp.Localization.Infrastructure;

public sealed class LocalizationDbContext(DbContextOptions<LocalizationDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), ILocalizationDbContext
{
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<Translation> Translations => Set<Translation>();
    public DbSet<TaxRule> TaxRules => Set<TaxRule>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<Currency>(b => { b.ToTable("Currencies"); b.HasIndex(x => x.Code).IsUnique(); b.Property(x => x.Code).HasMaxLength(3); });
        mb.Entity<ExchangeRate>(b =>
        {
            b.ToTable("ExchangeRates");
            b.Property(x => x.Rate).HasPrecision(18, 8);
            b.Property(x => x.BaseCurrency).HasMaxLength(3); b.Property(x => x.QuoteCurrency).HasMaxLength(3);
            b.HasIndex(x => new { x.BaseCurrency, x.QuoteCurrency, x.AsOfDate }).IsUnique();
        });
        mb.Entity<Translation>(b => { b.ToTable("Translations"); b.Property(x => x.Value).HasMaxLength(2000); b.HasIndex(x => new { x.Key, x.Culture }).IsUnique(); });
        mb.Entity<TaxRule>(b =>
        {
            b.ToTable("TaxRules");
            b.Property(x => x.Rate).HasPrecision(9, 6);
            b.HasIndex(x => new { x.TenantId, x.CountryCode, x.TaxCategory });
        });
    }
}

/// <summary>Pulls ECB reference rates through frankfurter.dev. Swap this class to use any paid/real-time provider.</summary>
public sealed class FrankfurterRateProvider(HttpClient http, IConfiguration cfg) : IExchangeRateProvider
{
    public async Task<RateSnapshot> GetLatestAsync(string baseCurrency, CancellationToken ct)
    {
        var url = string.Format(cfg["Localization:RatesApiUrl"] ?? "https://api.frankfurter.dev/v1/latest?base={0}", baseCurrency.ToUpperInvariant());
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
        var root = doc.RootElement;
        var rates = root.GetProperty("rates").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDecimal());
        return new RateSnapshot(root.GetProperty("base").GetString()!, DateOnly.Parse(root.GetProperty("date").GetString()!), rates);
    }
}

/// <summary>Scheduled refresh of exchange rates (Localization:RefreshMinutes, default 60).</summary>
public sealed class ExchangeRateRefresher(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<ExchangeRateRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        var every = TimeSpan.FromMinutes(cfg.GetValue("Localization:RefreshMinutes", 60));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                    .Send(new RefreshExchangeRatesCommand(cfg["Localization:BaseCurrency"] ?? "EUR"), ct);
                log.LogInformation("Exchange rates refreshed ({Changed} rows changed)", n);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Exchange rate refresh failed; will retry next cycle"); }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

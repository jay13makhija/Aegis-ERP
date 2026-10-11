namespace Erp.Localization.Application;

public interface ILocalizationDbContext : IUnitOfWork
{
    DbSet<Currency> Currencies { get; }
    DbSet<ExchangeRate> ExchangeRates { get; }
    DbSet<Translation> Translations { get; }
    DbSet<TaxRule> TaxRules { get; }
}
public sealed record RateSnapshot(string Base, DateOnly Date, Dictionary<string, decimal> Rates);
public interface IExchangeRateProvider { Task<RateSnapshot> GetLatestAsync(string baseCurrency, CancellationToken ct); }

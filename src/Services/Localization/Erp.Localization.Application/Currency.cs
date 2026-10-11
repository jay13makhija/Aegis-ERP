namespace Erp.Localization.Application;

public sealed record CurrencyDto(string Code, string Name, string Symbol, int DecimalPlaces, bool IsActive);
public sealed record ListCurrenciesQuery() : IQuery<List<CurrencyDto>>;
public sealed record UpsertCurrencyCommand(string Code, string Name, string Symbol, int DecimalPlaces, bool IsActive) : ICommand<Unit>;

internal sealed class ListCurrenciesHandler(ILocalizationDbContext db) : IQueryHandler<ListCurrenciesQuery, List<CurrencyDto>>
{
    public Task<List<CurrencyDto>> Handle(ListCurrenciesQuery q, CancellationToken ct) =>
        db.Currencies.AsNoTracking().OrderBy(c => c.Code).Select(c => new CurrencyDto(c.Code, c.Name, c.Symbol, c.DecimalPlaces, c.IsActive)).ToListAsync(ct);
}
internal sealed class UpsertCurrencyHandler(ILocalizationDbContext db) : ICommandHandler<UpsertCurrencyCommand, Unit>
{
    public async Task<Unit> Handle(UpsertCurrencyCommand c, CancellationToken ct)
    {
        var code = c.Code.ToUpperInvariant();
        var cur = await db.Currencies.FirstOrDefaultAsync(x => x.Code == code, ct);
        if (cur is null) db.Currencies.Add(new Currency(code, c.Name, c.Symbol, c.DecimalPlaces)); else cur.Update(c.Name, c.Symbol, c.DecimalPlaces, c.IsActive);
        return Unit.Value;
    }
}

public sealed record ConversionResult(string From, string To, decimal Amount, decimal Converted, decimal Rate, string BaseCurrency, DateOnly RateDate);
public sealed record ConvertCurrencyQuery(string From, string To, decimal Amount, DateOnly? AsOf) : IQuery<ConversionResult>;

/// <summary>Cross-rates through the stored base currency: converted = amount / rate(from) * rate(to), using the latest rate on or before AsOf.</summary>
internal sealed class ConvertCurrencyHandler(ILocalizationDbContext db) : IQueryHandler<ConvertCurrencyQuery, ConversionResult>
{
    public async Task<ConversionResult> Handle(ConvertCurrencyQuery q, CancellationToken ct)
    {
        var from = q.From.ToUpperInvariant(); var to = q.To.ToUpperInvariant();
        var asOf = q.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var baseCur = await db.ExchangeRates.AsNoTracking().OrderByDescending(r => r.AsOfDate).Select(r => r.BaseCurrency).FirstOrDefaultAsync(ct)
                      ?? throw new DomainException("No exchange rates loaded yet. POST /api/exchange-rates/refresh or wait for the scheduled refresh.");

        async Task<(decimal Rate, DateOnly Date)> RateOf(string code)
        {
            if (code == baseCur) return (1m, asOf);
            var r = await db.ExchangeRates.AsNoTracking().Where(x => x.BaseCurrency == baseCur && x.QuoteCurrency == code && x.AsOfDate <= asOf)
                .OrderByDescending(x => x.AsOfDate).Select(x => new { x.Rate, x.AsOfDate }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("ExchangeRate", $"{baseCur}/{code}");
            return (r.Rate, r.AsOfDate);
        }
        var (rf, df) = await RateOf(from); var (rt, dt) = await RateOf(to);
        var rate = rt / rf;
        var places = await db.Currencies.AsNoTracking().Where(c => c.Code == to).Select(c => (int?)c.DecimalPlaces).FirstOrDefaultAsync(ct) ?? 2;
        return new ConversionResult(from, to, q.Amount, Math.Round(q.Amount * rate, places, MidpointRounding.AwayFromZero), rate, baseCur, df < dt ? df : dt);
    }
}

public sealed record RefreshExchangeRatesCommand(string BaseCurrency) : ICommand<int>;

internal sealed class RefreshExchangeRatesHandler(ILocalizationDbContext db, IExchangeRateProvider provider) : ICommandHandler<RefreshExchangeRatesCommand, int>
{
    public async Task<int> Handle(RefreshExchangeRatesCommand c, CancellationToken ct)
    {
        var snap = await provider.GetLatestAsync(c.BaseCurrency, ct);
        var codes = snap.Rates.Keys.ToList();
        var existing = await db.ExchangeRates.Where(r => r.BaseCurrency == snap.Base && r.AsOfDate == snap.Date && codes.Contains(r.QuoteCurrency)).ToListAsync(ct);
        var known = await db.Currencies.Select(x => x.Code).ToListAsync(ct);
        var changed = 0;
        foreach (var (code, rate) in snap.Rates)
        {
            var row = existing.FirstOrDefault(r => r.QuoteCurrency == code);
            if (row is null) { db.ExchangeRates.Add(new ExchangeRate(snap.Base, code, rate, snap.Date, "frankfurter/ECB")); changed++; }
            else if (row.Rate != rate) { row.UpdateRate(rate); changed++; }
            if (!known.Contains(code)) { db.Currencies.Add(new Currency(code, code, code, 2)); known.Add(code); }
        }
        if (!known.Contains(snap.Base)) db.Currencies.Add(new Currency(snap.Base, snap.Base, snap.Base, 2));
        return changed;
    }
}

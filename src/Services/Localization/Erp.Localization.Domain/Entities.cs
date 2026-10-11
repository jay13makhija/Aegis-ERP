namespace Erp.Localization.Domain;

/// <summary>Platform-level reference data (shared by all tenants).</summary>
public sealed class Currency : AuditableEntity
{
    private Currency() { }
    public Currency(string code, string name, string symbol, int decimalPlaces) { Code = code.ToUpperInvariant(); Name = name; Symbol = symbol; DecimalPlaces = decimalPlaces; IsActive = true; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Symbol { get; private set; } = "";
    public int DecimalPlaces { get; private set; } = 2;
    public bool IsActive { get; private set; }
    public void Update(string name, string symbol, int decimalPlaces, bool active) { Name = name; Symbol = symbol; DecimalPlaces = decimalPlaces; IsActive = active; }
}

public sealed class ExchangeRate : AuditableEntity
{
    private ExchangeRate() { }
    public ExchangeRate(string baseCurrency, string quoteCurrency, decimal rate, DateOnly asOf, string source)
    { BaseCurrency = baseCurrency; QuoteCurrency = quoteCurrency; Rate = Guard.Positive(rate, nameof(rate)); AsOfDate = asOf; Source = source; }
    public string BaseCurrency { get; private set; } = "";
    public string QuoteCurrency { get; private set; } = "";
    public decimal Rate { get; private set; }
    public DateOnly AsOfDate { get; private set; }
    public string Source { get; private set; } = "";
    public void UpdateRate(decimal rate) => Rate = Guard.Positive(rate, nameof(rate));
}

public sealed class Translation : AuditableEntity
{
    private Translation() { }
    public Translation(string key, string culture, string value) { Key = key; Culture = culture; Value = value; }
    public string Key { get; private set; } = "";
    public string Culture { get; private set; } = "";
    public string Value { get; private set; } = "";
    public void SetValue(string value) => Value = value;
}

/// <summary>Tenant-configurable tax rule. Rate is a fraction (0.18 = 18 %). Rules apply in Priority order; compound rules tax the running total.</summary>
public sealed class TaxRule : TenantEntity
{
    private TaxRule() { }
    public TaxRule(string name, string countryCode, string? region, string category, decimal rate, int priority, bool isCompound, DateTime validFromUtc, DateTime? validToUtc)
    {
        Name = Guard.NotBlank(name, nameof(name)); CountryCode = Guard.NotBlank(countryCode, nameof(countryCode)).ToUpperInvariant(); Region = region?.Trim().ToUpperInvariant();
        TaxCategory = Guard.NotBlank(category, nameof(category)).ToLowerInvariant();
        Rate = rate is < 0 or > 1 ? throw new DomainException("rate must be a fraction between 0 and 1.") : rate;
        Priority = priority; IsCompound = isCompound; ValidFromUtc = validFromUtc; ValidToUtc = validToUtc;
    }
    public string Name { get; private set; } = "";
    public string CountryCode { get; private set; } = "";
    public string? Region { get; private set; }
    public string TaxCategory { get; private set; } = "standard";
    public decimal Rate { get; private set; }
    public int Priority { get; private set; }
    public bool IsCompound { get; private set; }
    public DateTime ValidFromUtc { get; private set; }
    public DateTime? ValidToUtc { get; private set; }
}

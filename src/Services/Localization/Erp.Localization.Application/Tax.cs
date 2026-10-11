namespace Erp.Localization.Application;

public sealed record TaxLine(string Name, decimal Rate, decimal Amount);
public sealed record TaxResult(decimal Net, decimal TaxTotal, decimal Gross, List<TaxLine> Lines);
public sealed record TaxRuleInput(string Name, decimal Rate, int Priority, bool IsCompound);

/// <summary>Pure tax maths (unit-tested). Handles stacked and compound taxes, and tax-inclusive prices.</summary>
public static class TaxCalculator
{
    public static TaxResult Calculate(decimal amount, bool amountIncludesTax, IEnumerable<TaxRuleInput> rules, int decimals = 2)
    {
        var ordered = rules.OrderBy(r => r.Priority).ToList();
        // gross = net * factor is linear, so inclusive prices are solved by computing the factor for net = 1
        var factor = 1m; var baseForCompound = 1m;
        foreach (var r in ordered)
        {
            var tax = (r.IsCompound ? baseForCompound : 1m) * r.Rate;
            factor += tax; baseForCompound += tax;
        }
        var net = amountIncludesTax ? amount / factor : amount;
        var lines = new List<TaxLine>(); var running = net;
        foreach (var r in ordered)
        {
            var tax = Math.Round((r.IsCompound ? running : net) * r.Rate, decimals, MidpointRounding.AwayFromZero);
            lines.Add(new TaxLine(r.Name, r.Rate, tax)); running += tax;
        }
        var netRounded = Math.Round(net, decimals, MidpointRounding.AwayFromZero);
        var total = lines.Sum(l => l.Amount);
        return new TaxResult(netRounded, total, netRounded + total, lines);
    }
}

public sealed record CalculateTaxQuery(string CountryCode, string? Region, string Category, decimal Amount, bool AmountIncludesTax, DateTime? AsOfUtc) : IQuery<TaxResult>;

internal sealed class CalculateTaxHandler(ILocalizationDbContext db) : IQueryHandler<CalculateTaxQuery, TaxResult>
{
    public async Task<TaxResult> Handle(CalculateTaxQuery q, CancellationToken ct)
    {
        var at = q.AsOfUtc ?? DateTime.UtcNow; var country = q.CountryCode.ToUpperInvariant();
        var region = q.Region?.ToUpperInvariant(); var cat = q.Category.ToLowerInvariant();
        var rules = await db.TaxRules.AsNoTracking()
            .Where(r => r.CountryCode == country && r.TaxCategory == cat && r.ValidFromUtc <= at && (r.ValidToUtc == null || r.ValidToUtc >= at)
                        && (r.Region == null || r.Region == region))
            .Select(r => new TaxRuleInput(r.Name, r.Rate, r.Priority, r.IsCompound)).ToListAsync(ct);
        return TaxCalculator.Calculate(q.Amount, q.AmountIncludesTax, rules);
    }
}

public sealed record CreateTaxRuleCommand(string Name, string CountryCode, string? Region, string Category, decimal Rate, int Priority, bool IsCompound, DateTime ValidFromUtc, DateTime? ValidToUtc) : ICommand<Guid>;
public sealed record TaxRuleDto(Guid Id, string Name, string CountryCode, string? Region, string Category, decimal Rate, int Priority, bool IsCompound, DateTime ValidFromUtc, DateTime? ValidToUtc);
public sealed record ListTaxRulesQuery(string? CountryCode) : IQuery<List<TaxRuleDto>>;

internal sealed class CreateTaxRuleHandler(ILocalizationDbContext db) : ICommandHandler<CreateTaxRuleCommand, Guid>
{
    public Task<Guid> Handle(CreateTaxRuleCommand c, CancellationToken ct)
    {
        var rule = new TaxRule(c.Name, c.CountryCode, c.Region, c.Category, c.Rate, c.Priority, c.IsCompound, c.ValidFromUtc, c.ValidToUtc);
        db.TaxRules.Add(rule);
        return Task.FromResult(rule.Id);
    }
}
internal sealed class ListTaxRulesHandler(ILocalizationDbContext db) : IQueryHandler<ListTaxRulesQuery, List<TaxRuleDto>>
{
    public Task<List<TaxRuleDto>> Handle(ListTaxRulesQuery q, CancellationToken ct)
    {
        var query = db.TaxRules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.CountryCode)) { var c = q.CountryCode.ToUpperInvariant(); query = query.Where(r => r.CountryCode == c); }
        return query.OrderBy(r => r.CountryCode).ThenBy(r => r.Priority)
            .Select(r => new TaxRuleDto(r.Id, r.Name, r.CountryCode, r.Region, r.TaxCategory, r.Rate, r.Priority, r.IsCompound, r.ValidFromUtc, r.ValidToUtc)).ToListAsync(ct);
    }
}

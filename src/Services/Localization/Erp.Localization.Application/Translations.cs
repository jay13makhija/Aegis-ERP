namespace Erp.Localization.Application;

public sealed record UpsertTranslationCommand(string Key, string Culture, string Value) : ICommand<Unit>;
public sealed record GetTranslationsQuery(string Culture) : IQuery<Dictionary<string, string>>;

internal sealed class UpsertTranslationHandler(ILocalizationDbContext db) : ICommandHandler<UpsertTranslationCommand, Unit>
{
    public async Task<Unit> Handle(UpsertTranslationCommand c, CancellationToken ct)
    {
        var culture = c.Culture.Trim();
        var t = await db.Translations.FirstOrDefaultAsync(x => x.Key == c.Key && x.Culture == culture, ct);
        if (t is null) db.Translations.Add(new Translation(c.Key, culture, c.Value)); else t.SetValue(c.Value);
        return Unit.Value;
    }
}

/// <summary>Resolution order per key: exact culture (fr-FR) → neutral culture (fr) → English.</summary>
internal sealed class GetTranslationsHandler(ILocalizationDbContext db) : IQueryHandler<GetTranslationsQuery, Dictionary<string, string>>
{
    public async Task<Dictionary<string, string>> Handle(GetTranslationsQuery q, CancellationToken ct)
    {
        var exact = q.Culture.Trim(); var neutral = exact.Split('-')[0];
        var rows = await db.Translations.AsNoTracking().Where(t => t.Culture == exact || t.Culture == neutral || t.Culture == "en")
            .Select(t => new { t.Key, t.Culture, t.Value }).ToListAsync(ct);
        int Rank(string c) => c == exact ? 0 : c == neutral ? 1 : 2;
        return rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.OrderBy(r => Rank(r.Culture)).First().Value);
    }
}

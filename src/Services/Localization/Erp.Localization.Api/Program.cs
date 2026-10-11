var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<LocalizationDbContext>("localization", typeof(ILocalizationDbContext).Assembly);
builder.Services.AddScoped<ILocalizationDbContext>(sp => sp.GetRequiredService<LocalizationDbContext>());
builder.Services.AddHttpClient<IExchangeRateProvider, FrankfurterRateProvider>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<ExchangeRateRefresher>();

var app = builder.Build();
await app.InitializeDatabaseAsync<LocalizationDbContext>();
app.UseErpService();

app.MapGet("/api/currencies", (IDispatcher d, CancellationToken ct) => d.Query(new ListCurrenciesQuery(), ct)).RequirePermission(Perm.LocalizationRead).WithTags("Currencies");
app.MapPut("/api/currencies", async (UpsertCurrencyCommand cmd, IDispatcher d, CancellationToken ct) => { await d.Send(cmd, ct); return Results.NoContent(); }).RequirePermission(Perm.LocalizationWrite).WithTags("Currencies");
app.MapGet("/api/exchange-rates/convert", (string from, string to, decimal amount, DateOnly? asOf, IDispatcher d, CancellationToken ct) =>
    d.Query(new ConvertCurrencyQuery(from, to, amount, asOf), ct)).RequirePermission(Perm.LocalizationRead).WithTags("Exchange rates");
app.MapPost("/api/exchange-rates/refresh", async (IConfiguration cfg, IDispatcher d, CancellationToken ct) =>
    Results.Ok(new { changed = await d.Send(new RefreshExchangeRatesCommand(cfg["Localization:BaseCurrency"] ?? "EUR"), ct) })).RequirePermission(Perm.LocalizationWrite).WithTags("Exchange rates");

app.MapGet("/api/tax/calculate", (string country, string? region, string category, decimal amount, bool? inclusive, DateTime? asOf, IDispatcher d, CancellationToken ct) =>
    d.Query(new CalculateTaxQuery(country, region, category, amount, inclusive ?? false, asOf), ct)).RequirePermission(Perm.LocalizationRead).WithTags("Tax");
app.MapGet("/api/tax/rules", (string? country, IDispatcher d, CancellationToken ct) => d.Query(new ListTaxRulesQuery(country), ct)).RequirePermission(Perm.LocalizationRead).WithTags("Tax");
app.MapPost("/api/tax/rules", async (CreateTaxRuleCommand cmd, IDispatcher d, CancellationToken ct) => Results.Created("/api/tax/rules", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.LocalizationWrite).WithTags("Tax");

// UI strings are needed before login, so reading them is anonymous. Writing needs a permission.
app.MapGet("/api/translations/{culture}", (string culture, IDispatcher d, CancellationToken ct) => d.Query(new GetTranslationsQuery(culture), ct)).AllowAnonymous().WithTags("Translations");
app.MapPut("/api/translations", async (UpsertTranslationCommand cmd, IDispatcher d, CancellationToken ct) => { await d.Send(cmd, ct); return Results.NoContent(); }).RequirePermission(Perm.LocalizationWrite).WithTags("Translations");

app.Run();

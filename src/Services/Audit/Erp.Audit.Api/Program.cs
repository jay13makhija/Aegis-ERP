var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<AuditDbContext>("audit", typeof(IAuditDbContext).Assembly);
builder.Services.AddScoped<IAuditDbContext>(sp => sp.GetRequiredService<AuditDbContext>());

var app = builder.Build();
await app.InitializeDatabaseAsync<AuditDbContext>();
app.UseErpService();

var g = app.MapGroup("/api/audit").WithTags("Audit");
g.MapGet("", (string? entityType, string? entityId, string? action, Guid? userId, DateTime? from, DateTime? to, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
    d.Query(new SearchAuditQuery(entityType, entityId, action, userId, from, to, page ?? 1, pageSize ?? 100), ct)).RequirePermission(Perm.AuditRead);
g.MapGet("/verify", (IDispatcher d, CancellationToken ct) => d.Query(new VerifyChainQuery(), ct)).RequirePermission(Perm.AuditRead);
app.Run();

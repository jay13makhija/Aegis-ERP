var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<ProcurementDbContext>("procurement", typeof(IProcurementDbContext).Assembly);
builder.Services.AddScoped<IProcurementDbContext>(sp => sp.GetRequiredService<ProcurementDbContext>());

var app = builder.Build();
await app.InitializeDatabaseAsync<ProcurementDbContext>();
app.UseErpService();

var sup = app.MapGroup("/api/suppliers").WithTags("Suppliers");
sup.MapGet("", (string? search, IDispatcher d, CancellationToken ct) => d.Query(new ListSuppliersQuery(search), ct)).RequirePermission(Perm.SuppliersRead);
sup.MapPost("", async (CreateSupplierCommand cmd, IDispatcher d, CancellationToken ct) => Results.Created("/api/suppliers", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.SuppliersWrite);

var po = app.MapGroup("/api/purchase-orders").WithTags("Purchase orders");
po.MapGet("", (PurchaseOrderStatus? status, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
    d.Query(new ListPurchaseOrdersQuery(status, page ?? 1, pageSize ?? 50), ct)).RequirePermission(Perm.PoRead);
po.MapGet("/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => d.Query(new GetPurchaseOrderQuery(id), ct)).RequirePermission(Perm.PoRead);
po.MapPost("", async (CreatePurchaseOrderCommand cmd, IDispatcher d, CancellationToken ct) => Results.Created("/api/purchase-orders", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.PoWrite);
po.MapPost("/{id:guid}/submit", async (Guid id, IDispatcher d, CancellationToken ct) => { await d.Send(new SubmitPurchaseOrderCommand(id), ct); return Results.Accepted(); }).RequirePermission(Perm.PoWrite);
po.MapPost("/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => { await d.Send(new CancelPurchaseOrderCommand(id), ct); return Results.NoContent(); }).RequirePermission(Perm.PoWrite);
po.MapPost("/{id:guid}/receive", async (Guid id, List<ReceiveLineInput> items, IDispatcher d, CancellationToken ct) =>
    Results.Ok(new { status = await d.Send(new ReceiveGoodsCommand(id, items), ct) })).RequirePermission(Perm.PoReceive);

app.Run();

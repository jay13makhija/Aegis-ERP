using Erp.Inventory.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<InventoryDbContext>("inventory", typeof(IInventoryDbContext).Assembly);
builder.Services.AddScoped<IInventoryDbContext>(sp => sp.GetRequiredService<InventoryDbContext>());
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IStockNotifier, SignalRStockNotifier>();
builder.Services.AddSignalR();
builder.Services.AddGrpc();

var app = builder.Build();
await app.InitializeDatabaseAsync<InventoryDbContext>();
app.UseErpService();

var products = app.MapGroup("/api/products").WithTags("Products");
products.MapGet("", (string? search, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
    d.Query(new ListProductsQuery(search, page ?? 1, pageSize ?? 50), ct)).RequirePermission(Perm.ProductsRead);
products.MapPost("", async (CreateProductCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/products", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.ProductsWrite);

var wh = app.MapGroup("/api/warehouses").WithTags("Warehouses");
wh.MapGet("", (IDispatcher d, CancellationToken ct) => d.Query(new ListWarehousesQuery(), ct)).RequirePermission(Perm.StockRead);
wh.MapPost("", async (CreateWarehouseCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/warehouses", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.ProductsWrite);

var stock = app.MapGroup("/api/stock").WithTags("Stock");
stock.MapGet("/levels", (Guid? productId, Guid? warehouseId, bool? lowStockOnly, IDispatcher d, CancellationToken ct) =>
    d.Query(new GetStockLevelsQuery(productId, warehouseId, lowStockOnly ?? false), ct)).RequirePermission(Perm.StockRead);
stock.MapGet("/movements", (Guid productId, Guid? warehouseId, DateTime? from, DateTime? to, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
    d.Query(new GetMovementsQuery(productId, warehouseId, from, to, page ?? 1, pageSize ?? 100), ct)).RequirePermission(Perm.StockRead);
stock.MapPost("/receive", async (ReceiveStockCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { movementId = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);
stock.MapPost("/issue", async (IssueStockCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { movementId = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);
stock.MapPost("/transfer", async (TransferStockCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { transferId = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);
stock.MapPost("/adjust", async (AdjustStockCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { movementId = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);
stock.MapPost("/reserve", async (ReserveStockCommand cmd, IDispatcher d, CancellationToken ct) => Results.Ok(new { available = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);

var ship = app.MapGroup("/api/shipments").WithTags("Logistics");
ship.MapPost("", async (CreateShipmentCommand cmd, IDispatcher d, CancellationToken ct) => Results.Created("/api/shipments", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.StockWrite);
ship.MapPost("/{id:guid}/status", async (Guid id, ShipmentStatusBody body, IDispatcher d, CancellationToken ct) =>
{ await d.Send(new UpdateShipmentStatusCommand(id, body.Status, body.Location, body.Note), ct); return Results.NoContent(); }).RequirePermission(Perm.StockWrite);
ship.MapGet("/track/{trackingNumber}", (string trackingNumber, IDispatcher d, CancellationToken ct) => d.Query(new GetShipmentQuery(trackingNumber), ct)).RequirePermission(Perm.StockRead);

app.MapHub<StockHub>("/hubs/stock");
app.MapGrpcService<StockQueryService>();
app.Run();

public sealed record ShipmentStatusBody(ShipmentStatus Status, string? Location, string? Note);

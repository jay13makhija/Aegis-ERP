namespace Erp.Inventory.Application;

public sealed record ProductDto(Guid Id, string Sku, string Name, string UnitOfMeasure, decimal StandardCost, string Currency, decimal ReorderLevel, string? Category, bool IsActive);
public sealed record CreateProductCommand(string Sku, string Name, string UnitOfMeasure, decimal StandardCost, string Currency,
    decimal ReorderLevel, string? Category, string? Description) : ICommand<Guid>;
public sealed record ListProductsQuery(string? Search, int Page, int PageSize) : IQuery<PagedResult<ProductDto>>;

internal sealed class CreateProductHandler(IInventoryDbContext db) : ICommandHandler<CreateProductCommand, Guid>
{
    public async Task<Guid> Handle(CreateProductCommand c, CancellationToken ct)
    {
        var sku = c.Sku.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(p => p.Sku == sku, ct)) throw new ConflictException($"SKU '{sku}' already exists.");
        var p = new Product(sku, c.Name, c.UnitOfMeasure, c.StandardCost, c.Currency, c.ReorderLevel, c.Category, c.Description);
        db.Products.Add(p);
        return p.Id;
    }
}

internal sealed class ListProductsHandler(IInventoryDbContext db) : IQueryHandler<ListProductsQuery, PagedResult<ProductDto>>
{
    public async Task<PagedResult<ProductDto>> Handle(ListProductsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 1, 200);
        var query = db.Products.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Search)) { var s = q.Search.Trim(); query = query.Where(p => p.Sku.Contains(s) || p.Name.Contains(s)); }
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.Sku).Skip((page - 1) * size).Take(size)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.UnitOfMeasure, p.StandardCost, p.Currency, p.ReorderLevel, p.Category, p.IsActive)).ToListAsync(ct);
        return new PagedResult<ProductDto>(items, page, size, total);
    }
}

public sealed record WarehouseDto(Guid Id, string Code, string Name, string? Address, bool IsActive);
public sealed record CreateWarehouseCommand(string Code, string Name, string? Address) : ICommand<Guid>;
public sealed record ListWarehousesQuery() : IQuery<List<WarehouseDto>>;

internal sealed class CreateWarehouseHandler(IInventoryDbContext db) : ICommandHandler<CreateWarehouseCommand, Guid>
{
    public async Task<Guid> Handle(CreateWarehouseCommand c, CancellationToken ct)
    {
        var code = c.Code.Trim().ToUpperInvariant();
        if (await db.Warehouses.AnyAsync(w => w.Code == code, ct)) throw new ConflictException($"Warehouse '{code}' already exists.");
        var w = new Warehouse(code, c.Name, c.Address);
        db.Warehouses.Add(w);
        return w.Id;
    }
}
internal sealed class ListWarehousesHandler(IInventoryDbContext db) : IQueryHandler<ListWarehousesQuery, List<WarehouseDto>>
{
    public Task<List<WarehouseDto>> Handle(ListWarehousesQuery q, CancellationToken ct) =>
        db.Warehouses.AsNoTracking().OrderBy(w => w.Code).Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.Address, w.IsActive)).ToListAsync(ct);
}

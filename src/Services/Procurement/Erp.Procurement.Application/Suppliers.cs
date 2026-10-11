namespace Erp.Procurement.Application;

public interface IProcurementDbContext : IUnitOfWork
{
    DbSet<Supplier> Suppliers { get; }
    DbSet<PurchaseOrder> PurchaseOrders { get; }
}

public sealed record SupplierDto(Guid Id, string Code, string Name, string? Email, string CountryCode, string Currency, int PaymentTermsDays, bool IsActive);
public sealed record CreateSupplierCommand(string Code, string Name, string? Email, string? Phone, string CountryCode, string Currency, int PaymentTermsDays) : ICommand<Guid>;
public sealed record ListSuppliersQuery(string? Search) : IQuery<List<SupplierDto>>;

internal sealed class CreateSupplierHandler(IProcurementDbContext db) : ICommandHandler<CreateSupplierCommand, Guid>
{
    public async Task<Guid> Handle(CreateSupplierCommand c, CancellationToken ct)
    {
        var code = c.Code.Trim().ToUpperInvariant();
        if (await db.Suppliers.AnyAsync(s => s.Code == code, ct)) throw new ConflictException($"Supplier '{code}' already exists.");
        var s = new Supplier(code, c.Name, c.Email, c.Phone, c.CountryCode, c.Currency, c.PaymentTermsDays);
        db.Suppliers.Add(s);
        return s.Id;
    }
}

internal sealed class ListSuppliersHandler(IProcurementDbContext db) : IQueryHandler<ListSuppliersQuery, List<SupplierDto>>
{
    public Task<List<SupplierDto>> Handle(ListSuppliersQuery q, CancellationToken ct)
    {
        var query = db.Suppliers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Search)) { var s = q.Search.Trim(); query = query.Where(x => x.Code.Contains(s) || x.Name.Contains(s)); }
        return query.OrderBy(x => x.Name).Take(500)
            .Select(x => new SupplierDto(x.Id, x.Code, x.Name, x.Email, x.CountryCode, x.Currency, x.PaymentTermsDays, x.IsActive)).ToListAsync(ct);
    }
}

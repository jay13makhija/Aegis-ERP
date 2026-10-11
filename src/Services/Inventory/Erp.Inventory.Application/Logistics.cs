namespace Erp.Inventory.Application;

public sealed record ShipmentEventDto(string Status, string? Location, string? Note, DateTime AtUtc);
public sealed record ShipmentDto(Guid Id, string Carrier, string TrackingNumber, string Destination, string Status, DateTime? ExpectedDeliveryUtc, List<ShipmentEventDto> History);
public sealed record CreateShipmentCommand(string Carrier, string TrackingNumber, Guid FromWarehouseId, string Destination, DateTime? ExpectedDeliveryUtc) : ICommand<Guid>;
public sealed record UpdateShipmentStatusCommand(Guid ShipmentId, ShipmentStatus Status, string? Location, string? Note) : ICommand<Unit>;
public sealed record GetShipmentQuery(string TrackingNumber) : IQuery<ShipmentDto>;

internal sealed class CreateShipmentHandler(IInventoryDbContext db) : ICommandHandler<CreateShipmentCommand, Guid>
{
    public async Task<Guid> Handle(CreateShipmentCommand c, CancellationToken ct)
    {
        if (!await db.Warehouses.AnyAsync(w => w.Id == c.FromWarehouseId, ct)) throw new NotFoundException(nameof(Warehouse), c.FromWarehouseId);
        var tracking = c.TrackingNumber.Trim();
        if (await db.Shipments.AnyAsync(s => s.TrackingNumber == tracking, ct)) throw new ConflictException("Tracking number already in use.");
        var s = new Shipment(c.Carrier, tracking, c.FromWarehouseId, c.Destination, c.ExpectedDeliveryUtc);
        db.Shipments.Add(s);
        return s.Id;
    }
}

internal sealed class UpdateShipmentStatusHandler(IInventoryDbContext db) : ICommandHandler<UpdateShipmentStatusCommand, Unit>
{
    public async Task<Unit> Handle(UpdateShipmentStatusCommand c, CancellationToken ct)
    {
        var s = await db.Shipments.FirstOrDefaultAsync(x => x.Id == c.ShipmentId, ct) ?? throw new NotFoundException(nameof(Shipment), c.ShipmentId);
        s.UpdateStatus(c.Status, c.Location, c.Note);
        return Unit.Value;
    }
}

internal sealed class GetShipmentHandler(IInventoryDbContext db) : IQueryHandler<GetShipmentQuery, ShipmentDto>
{
    public async Task<ShipmentDto> Handle(GetShipmentQuery q, CancellationToken ct)
    {
        var s = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(x => x.TrackingNumber == q.TrackingNumber, ct)
                ?? throw new NotFoundException(nameof(Shipment), q.TrackingNumber);
        return new ShipmentDto(s.Id, s.Carrier, s.TrackingNumber, s.Destination, s.Status.ToString(), s.ExpectedDeliveryUtc,
            s.History.OrderBy(h => h.AtUtc).Select(h => new ShipmentEventDto(h.Status.ToString(), h.Location, h.Note, h.AtUtc)).ToList());
    }
}

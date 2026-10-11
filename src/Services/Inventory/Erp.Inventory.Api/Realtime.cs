using System.Security.Claims;
using Erp.Inventory.Api.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
namespace Erp.Inventory.Api;

[Authorize]
public sealed class StockHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenant = Context.User?.FindFirstValue("tenant_id");
        if (tenant != null) await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenant}");
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRStockNotifier(IHubContext<StockHub> hub) : IStockNotifier
{
    public Task NotifyAsync(StockMovedEvent e, CancellationToken ct) =>
        hub.Clients.Group($"tenant:{e.TenantId}").SendAsync("stockMoved", e, ct);
}

[Authorize]
public sealed class StockQueryService(IDispatcher dispatcher) : StockQuery.StockQueryBase
{
    public override async Task<GetAvailableReply> GetAvailable(GetAvailableRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId)) throw new RpcException(new Status(StatusCode.InvalidArgument, "product_id must be a GUID"));
        Guid? warehouse = Guid.TryParse(request.WarehouseId, out var w) ? w : null;
        var levels = await dispatcher.Query(new GetStockLevelsQuery(productId, warehouse), context.CancellationToken);
        return new GetAvailableReply
        {
            ProductId = request.ProductId,
            OnHand = (double)levels.Sum(l => l.OnHand),
            Reserved = (double)levels.Sum(l => l.Reserved),
            Available = (double)levels.Sum(l => l.Available)
        };
    }
}

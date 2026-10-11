using System.Text.Json;
using Erp.BuildingBlocks.Domain;
namespace Erp.BuildingBlocks.Application;

public interface ICommand<TResult> { }
public interface IQuery<TResult> { }
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{ Task<TResult> Handle(TCommand command, CancellationToken ct); }
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{ Task<TResult> Handle(TQuery query, CancellationToken ct); }
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{ Task Handle(TEvent @event, CancellationToken ct); }

public interface IDispatcher
{
    /// <summary>Runs the command handler, then commits the unit of work (data + outbox + audit) in one transaction.</summary>
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

public sealed record Unit { public static readonly Unit Value = new(); }
public interface IUnitOfWork { Task<int> SaveChangesAsync(CancellationToken ct = default); }

public interface ITenantContext { Guid TenantId { get; } bool HasTenant { get; } void Set(Guid tenantId); }
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string UserName { get; }
    IReadOnlyCollection<string> Roles { get; }
    IReadOnlyCollection<string> Permissions { get; }
}

public interface IEventBus
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default);
    Task PublishRawAsync(string type, string payload, Guid tenantId, Guid eventId, CancellationToken ct = default);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "erp-identity";
    public string Audience { get; set; } = "erp-api";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

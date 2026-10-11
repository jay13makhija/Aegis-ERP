using System.Reflection;
using System.Runtime.ExceptionServices;
using Erp.BuildingBlocks.Domain;
using Microsoft.Extensions.DependencyInjection;
namespace Erp.BuildingBlocks.Application;

internal sealed class Dispatcher(IServiceProvider sp, IUnitOfWork uow) : IDispatcher
{
    public async Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        var result = await Invoke<TResult>(typeof(ICommandHandler<,>), command, ct);
        await uow.SaveChangesAsync(ct);
        return result;
    }

    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default)
        => Invoke<TResult>(typeof(IQueryHandler<,>), query, ct);

    private Task<TResult> Invoke<TResult>(Type openHandler, object request, CancellationToken ct)
    {
        var handlerType = openHandler.MakeGenericType(request.GetType(), typeof(TResult));
        var handler = sp.GetRequiredService(handlerType);
        try { return (Task<TResult>)handlerType.GetMethod("Handle")!.Invoke(handler, [request, ct])!; }
        catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }
}

public static class ApplicationRegistration
{
    /// <summary>Registers the dispatcher and every command/query/integration-event handler found in the assembly.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, Assembly assembly)
    {
        services.AddScoped<IDispatcher, Dispatcher>();
        Type[] open = [typeof(ICommandHandler<,>), typeof(IQueryHandler<,>), typeof(IIntegrationEventHandler<>)];
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
            foreach (var i in type.GetInterfaces().Where(i => i.IsGenericType && open.Contains(i.GetGenericTypeDefinition())))
                services.AddScoped(i, type);
        return services;
    }
}

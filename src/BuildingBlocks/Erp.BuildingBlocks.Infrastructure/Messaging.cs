using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
namespace Erp.BuildingBlocks.Infrastructure;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string User { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string Exchange { get; set; } = "erp.events";
}

internal static class RabbitMq
{
    public static Task<IConnection> ConnectAsync(RabbitMqOptions o, CancellationToken ct) =>
        new ConnectionFactory { HostName = o.Host, Port = o.Port, UserName = o.User, Password = o.Password }.CreateConnectionAsync(ct);
}

public sealed class EventSubscriptions
{
    private readonly Dictionary<string, Type> _map = new();
    public void Add(Type eventType) => _map[eventType.Name] = eventType;
    public IEnumerable<string> Names => _map.Keys;
    public int Count => _map.Count;
    public bool TryGet(string name, out Type type) => _map.TryGetValue(name, out type!);
}

/// <summary>Topic exchange publisher with publisher confirms. Routing key = event type name.</summary>
public sealed class RabbitMqEventBus(IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventBus> log) : IEventBus, IAsyncDisposable
{
    private readonly RabbitMqOptions _o = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _conn;
    private IChannel? _channel;

    public Task PublishAsync(IntegrationEvent e, CancellationToken ct = default) =>
        PublishRawAsync(e.GetType().Name, JsonSerializer.Serialize(e, e.GetType(), JsonDefaults.Options), e.TenantId, e.EventId, ct);

    public async Task PublishRawAsync(string type, string payload, Guid tenantId, Guid eventId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var ch = await GetChannelAsync(ct);
            var props = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                Type = type,
                MessageId = eventId.ToString(),
                Headers = new Dictionary<string, object?> { ["tenant-id"] = tenantId.ToString() }
            };
            await ch.BasicPublishAsync(_o.Exchange, type, false, props, Encoding.UTF8.GetBytes(payload), ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Publish of {Type} failed; resetting connection", type);
            await ResetAsync();
            throw;
        }
        finally { _lock.Release(); }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await ResetAsync();
        _conn = await RabbitMq.ConnectAsync(_o, ct);
        _channel = await _conn.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await _channel.ExchangeDeclareAsync(_o.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        return _channel;
    }

    private async Task ResetAsync()
    {
        try { if (_channel != null) await _channel.DisposeAsync(); } catch { }
        try { if (_conn != null) await _conn.DisposeAsync(); } catch { }
        _channel = null; _conn = null;
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}

/// <summary>Reads the outbox and publishes to the broker (at-least-once; consumers are idempotent through the inbox).</summary>
public sealed class OutboxDispatcher<TContext>(IServiceScopeFactory scopes, ILogger<OutboxDispatcher<TContext>> log) : BackgroundService
    where TContext : ErpDbContext
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var count = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                var batch = await db.OutboxMessages.Where(x => x.ProcessedAtUtc == null && x.Attempts < 20)
                    .OrderBy(x => x.OccurredAtUtc).Take(100).ToListAsync(ct);
                foreach (var m in batch)
                {
                    try { await bus.PublishRawAsync(m.Type, m.Payload, m.TenantId, m.Id, ct); m.ProcessedAtUtc = DateTime.UtcNow; }
                    catch (Exception ex)
                    {
                        m.Attempts++; m.Error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
                        break; // keep ordering; retry next loop
                    }
                }
                await db.SaveChangesAsync(ct);
                count = batch.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Outbox dispatch loop error"); }

            if (count == 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }
}

/// <summary>Queue per service (erp.&lt;service&gt;) bound to the event types that service handles; failed messages go to a dead-letter queue after 3 in-process retries.</summary>
public sealed class RabbitMqConsumer<TContext>(IOptions<RabbitMqOptions> options, EventSubscriptions subs, ServiceInfo info,
    IServiceScopeFactory scopes, ILogger<RabbitMqConsumer<TContext>> log) : BackgroundService where TContext : ErpDbContext
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (subs.Count == 0) return;
        while (!ct.IsCancellationRequested)
        {
            try { await RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Consumer connection lost/failed; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var queue = $"erp.{info.Name}";
        await using var conn = await RabbitMq.ConnectAsync(o, ct);
        var closed = new TaskCompletionSource();
        conn.ConnectionShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
        await using var ch = await conn.CreateChannelAsync(cancellationToken: ct);

        await ch.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(o.Exchange + ".dlx", ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await ch.QueueDeclareAsync(queue + ".dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await ch.QueueBindAsync(queue + ".dlq", o.Exchange + ".dlx", "", cancellationToken: ct);
        await ch.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = o.Exchange + ".dlx" }, cancellationToken: ct);
        foreach (var name in subs.Names) await ch.QueueBindAsync(queue, o.Exchange, name, cancellationToken: ct);
        await ch.BasicQosAsync(0, 10, false, ct);

        var consumer = new AsyncEventingBasicConsumer(ch);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var type = ea.BasicProperties.Type ?? ea.RoutingKey;
            var payload = Encoding.UTF8.GetString(ea.Body.ToArray());
            try
            {
                await HandleWithRetryAsync(type, payload, ct);
                await ch.BasicAckAsync(ea.DeliveryTag, false, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Message {Type} failed permanently; dead-lettering", type);
                await ch.BasicNackAsync(ea.DeliveryTag, false, false, ct);
            }
        };
        await ch.BasicConsumeAsync(queue, autoAck: false, consumer: consumer, cancellationToken: ct);
        log.LogInformation("Consuming {Queue}: {Events}", queue, string.Join(", ", subs.Names));

        using var reg = ct.Register(() => closed.TrySetCanceled());
        await closed.Task;
    }

    private async Task HandleWithRetryAsync(string type, string payload, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await HandleAsync(type, payload, ct); return; }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Handling {Type} failed (attempt {Attempt})", type, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }

    private async Task HandleAsync(string type, string payload, CancellationToken ct)
    {
        if (!subs.TryGet(type, out var eventType)) return;
        var ev = (IntegrationEvent)JsonSerializer.Deserialize(payload, eventType, JsonDefaults.Options)!;

        using var scope = scopes.CreateScope();      // fresh scope (and DbContext) per attempt
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().Set(ev.TenantId);
        var db = sp.GetRequiredService<TContext>();
        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
        var method = handlerType.GetMethod("Handle")!;

        foreach (var handler in sp.GetServices(handlerType))
        {
            var name = handler!.GetType().FullName!;
            if (await db.InboxMessages.AnyAsync(i => i.EventId == ev.EventId && i.Handler == name, ct)) continue;
            try { await (Task)method.Invoke(handler, [ev, ct])!; }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); }
            db.InboxMessages.Add(new InboxMessage { EventId = ev.EventId, Handler = name, ProcessedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);   // handler changes + inbox row commit atomically
        }
    }
}

public static class MessagingRegistration
{
    /// <summary>Call after AddApplication so integration-event handlers are already registered.</summary>
    public static IServiceCollection AddMessaging<TContext>(this IServiceCollection services, IConfiguration cfg) where TContext : ErpDbContext
    {
        services.Configure<RabbitMqOptions>(cfg.GetSection("RabbitMq"));
        var subs = new EventSubscriptions();
        foreach (var d in services.Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            subs.Add(d.ServiceType.GetGenericArguments()[0]);
        services.AddSingleton(subs);
        services.AddSingleton<IEventBus, RabbitMqEventBus>();
        services.AddHostedService<OutboxDispatcher<TContext>>();
        services.AddHostedService<RabbitMqConsumer<TContext>>();
        return services;
    }
}

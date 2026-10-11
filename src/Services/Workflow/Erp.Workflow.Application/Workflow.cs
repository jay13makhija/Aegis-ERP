namespace Erp.Workflow.Application;

public interface IWorkflowDbContext : IUnitOfWork
{
    DbSet<WorkflowDefinition> Definitions { get; }
    DbSet<WorkflowInstance> Instances { get; }
}

public sealed class WorkflowStarter(IWorkflowDbContext db)
{
    public async Task<WorkflowInstance> StartAsync(string entityType, Guid entityId, string payloadJson, string startedBy, CancellationToken ct)
    {
        var def = await db.Definitions.Where(d => d.EntityType == entityType && d.IsActive).OrderByDescending(d => d.Version).FirstOrDefaultAsync(ct);
        var instance = WorkflowInstance.Start(def, entityType, entityId, payloadJson, startedBy);
        db.Instances.Add(instance);
        return instance;
    }
}

public sealed record StepInput(int Order, string Name, string? ApproverRole, Guid? ApproverUserId, string? ConditionsJson);
public sealed record CreateWorkflowDefinitionCommand(string Code, string Name, string EntityType, List<StepInput> Steps) : ICommand<Guid>;
public sealed record StartWorkflowCommand(string EntityType, Guid EntityId, string PayloadJson) : ICommand<Guid>;
public sealed record DecideTaskCommand(Guid InstanceId, bool Approve, string? Comment) : ICommand<string>;
public sealed record CancelWorkflowCommand(Guid InstanceId) : ICommand<Unit>;

internal sealed class CreateDefinitionHandler(IWorkflowDbContext db) : ICommandHandler<CreateWorkflowDefinitionCommand, Guid>
{
    public async Task<Guid> Handle(CreateWorkflowDefinitionCommand c, CancellationToken ct)
    {
        var code = c.Code.Trim().ToLowerInvariant();
        var previous = await db.Definitions.Where(d => d.Code == code).ToListAsync(ct);
        foreach (var p in previous) p.Deactivate();                       // new version supersedes older ones; running instances keep their tasks
        var def = new WorkflowDefinition(code, c.Name, c.EntityType, previous.Count == 0 ? 1 : previous.Max(p => p.Version) + 1,
            c.Steps.Select(s => new WorkflowStepDefinition(s.Order, s.Name, s.ApproverRole, s.ApproverUserId, s.ConditionsJson)));
        db.Definitions.Add(def);
        return def.Id;
    }
}

internal sealed class StartWorkflowHandler(WorkflowStarter starter, ICurrentUser me) : ICommandHandler<StartWorkflowCommand, Guid>
{
    public async Task<Guid> Handle(StartWorkflowCommand c, CancellationToken ct) =>
        (await starter.StartAsync(c.EntityType, c.EntityId, c.PayloadJson, me.UserName, ct)).Id;
}

internal sealed class DecideTaskHandler(IWorkflowDbContext db, ICurrentUser me) : ICommandHandler<DecideTaskCommand, string>
{
    public async Task<string> Handle(DecideTaskCommand c, CancellationToken ct)
    {
        var i = await db.Instances.FirstOrDefaultAsync(x => x.Id == c.InstanceId, ct) ?? throw new NotFoundException(nameof(WorkflowInstance), c.InstanceId);
        i.Decide(me.UserId ?? throw new UnauthorizedAccessException(), me.UserName, me.Roles, c.Approve, c.Comment);
        return i.Status.ToString();
    }
}

internal sealed class CancelWorkflowHandler(IWorkflowDbContext db, ICurrentUser me) : ICommandHandler<CancelWorkflowCommand, Unit>
{
    public async Task<Unit> Handle(CancelWorkflowCommand c, CancellationToken ct)
    {
        var i = await db.Instances.FirstOrDefaultAsync(x => x.Id == c.InstanceId, ct) ?? throw new NotFoundException(nameof(WorkflowInstance), c.InstanceId);
        i.Cancel(me.UserName);
        return Unit.Value;
    }
}

public sealed record TaskDto(Guid Id, int StepOrder, string StepName, string? ApproverRole, string Status, string? DecidedBy, DateTime? DecidedAtUtc, string? Comment);
public sealed record InstanceDto(Guid Id, string EntityType, Guid EntityId, string Status, string StartedBy, DateTime StartedAtUtc, DateTime? CompletedAtUtc, List<TaskDto> Tasks);
public sealed record GetInstanceQuery(Guid? InstanceId, string? EntityType, Guid? EntityId) : IQuery<InstanceDto>;
public sealed record MyTasksQuery() : IQuery<List<InstanceDto>>;
public sealed record DefinitionDto(Guid Id, string Code, string Name, string EntityType, int Version, bool IsActive, List<StepInput> Steps);
public sealed record ListDefinitionsQuery() : IQuery<List<DefinitionDto>>;

internal static class Mapping
{
    public static InstanceDto ToDto(WorkflowInstance i) => new(i.Id, i.EntityType, i.EntityId, i.Status.ToString(), i.StartedBy, i.StartedAtUtc, i.CompletedAtUtc,
        i.Tasks.OrderBy(t => t.StepOrder).Select(t => new TaskDto(t.Id, t.StepOrder, t.StepName, t.ApproverRole, t.Status.ToString(), t.DecidedBy, t.DecidedAtUtc, t.Comment)).ToList());
}

internal sealed class GetInstanceHandler(IWorkflowDbContext db) : IQueryHandler<GetInstanceQuery, InstanceDto>
{
    public async Task<InstanceDto> Handle(GetInstanceQuery q, CancellationToken ct)
    {
        var query = db.Instances.AsNoTracking();
        var i = q.InstanceId != null
            ? await query.FirstOrDefaultAsync(x => x.Id == q.InstanceId, ct)
            : await query.Where(x => x.EntityType == q.EntityType && x.EntityId == q.EntityId).OrderByDescending(x => x.StartedAtUtc).FirstOrDefaultAsync(ct);
        return i is null ? throw new NotFoundException(nameof(WorkflowInstance), q.InstanceId?.ToString() ?? $"{q.EntityType}/{q.EntityId}") : Mapping.ToDto(i);
    }
}

/// <summary>Approval inbox: running instances whose ACTIVE step is assigned to the caller (by role or by name).</summary>
internal sealed class MyTasksHandler(IWorkflowDbContext db, ICurrentUser me) : IQueryHandler<MyTasksQuery, List<InstanceDto>>
{
    public async Task<List<InstanceDto>> Handle(MyTasksQuery q, CancellationToken ct)
    {
        var roles = me.Roles.ToList(); var uid = me.UserId;
        var candidates = await db.Instances.AsNoTracking()
            .Where(i => i.Status == WorkflowStatus.Running && i.Tasks.Any(t => t.Status == StepTaskStatus.Pending &&
                        ((t.ApproverRole != null && roles.Contains(t.ApproverRole)) || t.ApproverUserId == uid)))
            .OrderBy(i => i.StartedAtUtc).Take(200).ToListAsync(ct);
        return candidates.Where(i => i.ActiveTask is { } t && uid != null && t.CanBeDecidedBy(uid.Value, roles)).Select(Mapping.ToDto).ToList();
    }
}

internal sealed class ListDefinitionsHandler(IWorkflowDbContext db) : IQueryHandler<ListDefinitionsQuery, List<DefinitionDto>>
{
    public async Task<List<DefinitionDto>> Handle(ListDefinitionsQuery q, CancellationToken ct)
    {
        var defs = await db.Definitions.AsNoTracking().OrderBy(d => d.Code).ThenByDescending(d => d.Version).ToListAsync(ct);
        return defs.Select(d => new DefinitionDto(d.Id, d.Code, d.Name, d.EntityType, d.Version, d.IsActive,
            d.Steps.OrderBy(s => s.Order).Select(s => new StepInput(s.Order, s.Name, s.ApproverRole, s.ApproverUserId, s.ConditionsJson)).ToList())).ToList();
    }
}

/// <summary>Procurement → Workflow: a submitted PO starts its approval chain (rules decide which steps apply, e.g. amount thresholds).</summary>
internal sealed class PurchaseOrderSubmittedHandler(WorkflowStarter starter) : IIntegrationEventHandler<PurchaseOrderSubmittedEvent>
{
    public async Task Handle(PurchaseOrderSubmittedEvent e, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { e.PurchaseOrderId, e.Number, e.SupplierId, e.Currency, totalAmount = e.TotalAmount }, JsonDefaults.Options);
        await starter.StartAsync("PurchaseOrder", e.PurchaseOrderId, payload, e.RequestedBy, ct);
    }
}

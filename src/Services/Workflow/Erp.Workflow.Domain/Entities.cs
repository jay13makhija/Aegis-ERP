using System.Text.Json;
namespace Erp.Workflow.Domain;

public sealed record RuleCondition(string Field, string Operator, JsonElement Value);

/// <summary>
/// Rule engine for step conditions. A condition list is a JSON array evaluated with AND against the entity payload, e.g.
/// [{"field":"totalAmount","operator":"gt","value":10000}]. Operators: eq, ne, gt, gte, lt, lte, contains, in.
/// </summary>
public static class RuleEvaluator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Evaluate(string? conditionsJson, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(conditionsJson)) return true;
        var conditions = JsonSerializer.Deserialize<List<RuleCondition>>(conditionsJson, Json) ?? [];
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
        foreach (var c in conditions)
        {
            if (!TryGet(doc.RootElement, c.Field, out var actual)) return false;
            if (!Compare(actual, c.Operator.ToLowerInvariant(), c.Value)) return false;
        }
        return true;
    }

    private static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var found = false;
            foreach (var p in value.EnumerateObject())
                if (string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase)) { value = p.Value; found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private static bool Compare(JsonElement actual, string op, JsonElement expected)
    {
        if (op == "in") return expected.ValueKind == JsonValueKind.Array && expected.EnumerateArray().Any(e => Equal(actual, e));
        if (op == "contains") return actual.ToString().Contains(expected.ToString(), StringComparison.OrdinalIgnoreCase);
        if (op == "eq") return Equal(actual, expected);
        if (op == "ne") return !Equal(actual, expected);
        if (actual.ValueKind != JsonValueKind.Number || expected.ValueKind != JsonValueKind.Number) return false;
        var a = actual.GetDecimal(); var b = expected.GetDecimal();
        return op switch { "gt" => a > b, "gte" => a >= b, "lt" => a < b, "lte" => a <= b, _ => throw new DomainException($"Unknown operator '{op}'.") };
    }

    private static bool Equal(JsonElement a, JsonElement b) =>
        a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number
            ? a.GetDecimal() == b.GetDecimal()
            : string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
}

public sealed class WorkflowStepDefinition
{
    private WorkflowStepDefinition() { }
    public WorkflowStepDefinition(int order, string name, string? approverRole, Guid? approverUserId, string? conditionsJson)
    {
        if (approverRole is null && approverUserId is null) throw new DomainException($"Step '{name}' needs an approver role or user.");
        Order = order; Name = Guard.NotBlank(name, nameof(name)); ApproverRole = approverRole; ApproverUserId = approverUserId; ConditionsJson = conditionsJson;
        if (conditionsJson != null)   // fail fast on malformed rule JSON
        { try { RuleEvaluator.Evaluate(conditionsJson, "{}"); } catch (JsonException) { throw new DomainException($"Step '{name}' has invalid conditions JSON."); } }
    }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public int Order { get; private set; }
    public string Name { get; private set; } = "";
    public string? ApproverRole { get; private set; }
    public Guid? ApproverUserId { get; private set; }
    public string? ConditionsJson { get; private set; }
}

public sealed class WorkflowDefinition : TenantEntity
{
    private readonly List<WorkflowStepDefinition> _steps = [];
    private WorkflowDefinition() { }
    public WorkflowDefinition(string code, string name, string entityType, int version, IEnumerable<WorkflowStepDefinition> steps)
    {
        Code = Guard.NotBlank(code, nameof(code)).ToLowerInvariant(); Name = Guard.NotBlank(name, nameof(name));
        EntityType = Guard.NotBlank(entityType, nameof(entityType)); Version = version; IsActive = true;
        _steps.AddRange(steps);
        if (_steps.Count == 0) throw new DomainException("A workflow needs at least one step.");
    }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public int Version { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyCollection<WorkflowStepDefinition> Steps => _steps;
    public void Deactivate() => IsActive = false;
}

public enum WorkflowStatus { Running, Approved, Rejected, Cancelled }
public enum StepTaskStatus { Pending, Approved, Rejected }

public sealed class WorkflowTask
{
    private WorkflowTask() { }
    public WorkflowTask(int stepOrder, string stepName, string? approverRole, Guid? approverUserId)
    { StepOrder = stepOrder; StepName = stepName; ApproverRole = approverRole; ApproverUserId = approverUserId; Status = StepTaskStatus.Pending; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public int StepOrder { get; private set; }
    public string StepName { get; private set; } = "";
    public string? ApproverRole { get; private set; }
    public Guid? ApproverUserId { get; private set; }
    public StepTaskStatus Status { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? Comment { get; private set; }

    public bool CanBeDecidedBy(Guid userId, IReadOnlyCollection<string> roles) =>
        (ApproverUserId.HasValue && ApproverUserId == userId) || (ApproverRole != null && roles.Contains(ApproverRole)) || roles.Contains("Administrator");
    internal void Decide(bool approve, string by, string? comment) { Status = approve ? StepTaskStatus.Approved : StepTaskStatus.Rejected; DecidedBy = by; Comment = comment; DecidedAtUtc = DateTime.UtcNow; }
}

public sealed class WorkflowInstance : TenantEntity
{
    private readonly List<WorkflowTask> _tasks = [];
    private WorkflowInstance() { }
    private WorkflowInstance(Guid? definitionId, string entityType, Guid entityId, string payloadJson, string startedBy)
    { DefinitionId = definitionId; EntityType = entityType; EntityId = entityId; PayloadJson = payloadJson; StartedBy = startedBy; StartedAtUtc = DateTime.UtcNow; Status = WorkflowStatus.Running; }

    public Guid? DefinitionId { get; private set; }
    public string EntityType { get; private set; } = "";
    public Guid EntityId { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public string StartedBy { get; private set; } = "";
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public WorkflowStatus Status { get; private set; }
    public IReadOnlyCollection<WorkflowTask> Tasks => _tasks;

    /// <summary>Evaluates each step's rule against the payload; steps whose rule fails are skipped. No definition or no matching step ⇒ auto-approved.</summary>
    public static WorkflowInstance Start(WorkflowDefinition? def, string entityType, Guid entityId, string payloadJson, string startedBy)
    {
        var i = new WorkflowInstance(def?.Id, entityType, entityId, payloadJson, startedBy);
        if (def is null) { i.Complete(WorkflowStatus.Approved, "system", "No workflow defined for this entity type; auto-approved."); return i; }
        foreach (var s in def.Steps.OrderBy(s => s.Order).Where(s => RuleEvaluator.Evaluate(s.ConditionsJson, payloadJson)))
            i._tasks.Add(new WorkflowTask(s.Order, s.Name, s.ApproverRole, s.ApproverUserId));
        if (i._tasks.Count == 0) i.Complete(WorkflowStatus.Approved, "system", "No approval step matched the rules; auto-approved.");
        return i;
    }

    public WorkflowTask? ActiveTask => _tasks.Where(t => t.Status == StepTaskStatus.Pending).OrderBy(t => t.StepOrder).FirstOrDefault();

    public void Decide(Guid userId, string userName, IReadOnlyCollection<string> roles, bool approve, string? comment)
    {
        if (Status != WorkflowStatus.Running) throw new DomainException($"Workflow is already {Status}.");
        var task = ActiveTask ?? throw new DomainException("No pending task.");
        if (!task.CanBeDecidedBy(userId, roles)) throw new ForbiddenException($"You are not an approver for step '{task.StepName}'.");
        if (string.Equals(StartedBy, userName, StringComparison.OrdinalIgnoreCase)) throw new ForbiddenException("Segregation of duties: the requester cannot approve their own request.");
        task.Decide(approve, userName, comment);
        if (!approve) Complete(WorkflowStatus.Rejected, userName, comment);
        else if (_tasks.All(t => t.Status == StepTaskStatus.Approved)) Complete(WorkflowStatus.Approved, userName, comment);
    }

    public void Cancel(string by) { if (Status == WorkflowStatus.Running) Complete(WorkflowStatus.Cancelled, by, "Cancelled"); }

    private void Complete(WorkflowStatus outcome, string by, string? comment)
    {
        Status = outcome; CompletedAtUtc = DateTime.UtcNow;
        Raise(new WorkflowCompletedEvent(Id, EntityType, EntityId, outcome.ToString(), by, comment));
    }
}

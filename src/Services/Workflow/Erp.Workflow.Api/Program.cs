var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<WorkflowDbContext>("workflow", typeof(IWorkflowDbContext).Assembly);
builder.Services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<WorkflowDbContext>());
builder.Services.AddScoped<WorkflowStarter>();

var app = builder.Build();
await app.InitializeDatabaseAsync<WorkflowDbContext>();
app.UseErpService();

var g = app.MapGroup("/api/workflows").WithTags("Workflow");
g.MapGet("/definitions", (IDispatcher d, CancellationToken ct) => d.Query(new ListDefinitionsQuery(), ct)).RequirePermission(Perm.WorkflowRead);
g.MapPost("/definitions", async (CreateWorkflowDefinitionCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/workflows/definitions", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.WorkflowWrite);
g.MapPost("/instances", async (StartWorkflowCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/workflows/instances", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.WorkflowWrite);
g.MapGet("/instances/{id:guid}", (Guid id, IDispatcher d, CancellationToken ct) => d.Query(new GetInstanceQuery(id, null, null), ct)).RequirePermission(Perm.WorkflowRead);
g.MapGet("/instances/by-entity", (string entityType, Guid entityId, IDispatcher d, CancellationToken ct) => d.Query(new GetInstanceQuery(null, entityType, entityId), ct)).RequirePermission(Perm.WorkflowRead);
g.MapGet("/tasks/mine", (IDispatcher d, CancellationToken ct) => d.Query(new MyTasksQuery(), ct)).RequirePermission(Perm.WorkflowDecide);
g.MapPost("/instances/{id:guid}/decision", async (Guid id, DecisionBody body, IDispatcher d, CancellationToken ct) =>
    Results.Ok(new { status = await d.Send(new DecideTaskCommand(id, body.Approve, body.Comment), ct) })).RequirePermission(Perm.WorkflowDecide);
g.MapPost("/instances/{id:guid}/cancel", async (Guid id, IDispatcher d, CancellationToken ct) => { await d.Send(new CancelWorkflowCommand(id), ct); return Results.NoContent(); }).RequirePermission(Perm.WorkflowWrite);

app.Run();

public sealed record DecisionBody(bool Approve, string? Comment);

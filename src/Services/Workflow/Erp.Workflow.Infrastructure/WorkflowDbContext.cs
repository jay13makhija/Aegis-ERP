namespace Erp.Workflow.Infrastructure;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IWorkflowDbContext
{
    public DbSet<WorkflowDefinition> Definitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowInstance> Instances => Set<WorkflowInstance>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<WorkflowDefinition>(b =>
        {
            b.ToTable("WorkflowDefinitions");
            b.HasIndex(x => new { x.TenantId, x.Code, x.Version }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.IsActive });
            b.OwnsMany(x => x.Steps, s =>
            {
                s.ToTable("WorkflowStepDefinitions");
                s.WithOwner().HasForeignKey("DefinitionId");
                s.HasKey(x => x.Id);
                s.Property(x => x.ConditionsJson).HasColumnType("nvarchar(max)");
            });
            b.Navigation(x => x.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        mb.Entity<WorkflowInstance>(b =>
        {
            b.ToTable("WorkflowInstances");
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.Ignore(x => x.ActiveTask);
            b.OwnsMany(x => x.Tasks, t =>
            {
                t.ToTable("WorkflowTasks");
                t.WithOwner().HasForeignKey("InstanceId");
                t.HasKey(x => x.Id);
                t.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
                t.Property(x => x.Comment).HasMaxLength(1000);
            });
            b.Navigation(x => x.Tasks).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}

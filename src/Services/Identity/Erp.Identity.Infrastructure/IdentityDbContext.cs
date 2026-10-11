using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Erp.Identity.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant, ICurrentUser user, ServiceInfo service)
    : ErpDbContext(options, tenant, user, service), IIdentityDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureModel(ModelBuilder mb)
    {
        mb.Entity<Tenant>(b => { b.ToTable("Tenants"); b.HasIndex(x => x.Code).IsUnique(); });
        mb.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasIndex(x => new { x.TenantId, x.Email }).IsUnique().HasFilter("[IsDeleted] = 0");
            b.Property(x => x.PasswordHash).HasMaxLength(512);
            b.Property(x => x.MfaSecret).HasMaxLength(512).IsEncrypted();
        });
        mb.Entity<Role>(b => { b.ToTable("Roles"); b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[IsDeleted] = 0"); });
        mb.Entity<RefreshToken>(b => { b.ToTable("RefreshTokens"); b.HasIndex(x => x.TokenHash).IsUnique(); b.HasIndex(x => x.UserId); b.Property(x => x.TokenHash).HasMaxLength(128); });
    }
}

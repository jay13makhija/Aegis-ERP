namespace Erp.Identity.Application;

public sealed record TenantDto(Guid Id, string Code);
public sealed record RegisterTenantCommand(string Code, string Name, string DefaultCurrency, string DefaultCulture,
    string AdminEmail, string AdminFullName, string AdminPassword) : ICommand<TenantDto>;

internal sealed class RegisterTenantHandler(IIdentityDbContext db, ITenantContext tenantCtx, IPasswordService pw)
    : ICommandHandler<RegisterTenantCommand, TenantDto>
{
    public async Task<TenantDto> Handle(RegisterTenantCommand c, CancellationToken ct)
    {
        var code = c.Code.Trim().ToLowerInvariant();
        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Code == code, ct)) throw new ConflictException($"Tenant '{code}' already exists.");
        if (c.AdminPassword.Length < 12) throw new DomainException("Password must be at least 12 characters.");

        var tenant = new Tenant(code, c.Name, c.DefaultCurrency, c.DefaultCulture);
        tenantCtx.Set(tenant.Id);                       // everything below is stamped with the new tenant
        var admin = new Role("Administrator", "Full access", ["*"], isSystem: true);
        var user = new User(c.AdminEmail, c.AdminFullName, pw.Hash(c.AdminPassword));
        user.SetRoles([admin.Id]);
        db.Tenants.Add(tenant); db.Roles.Add(admin); db.Users.Add(user);
        return new TenantDto(tenant.Id, tenant.Code);
    }
}

public sealed record UserDto(Guid Id, string Email, string FullName, bool IsActive, bool MfaEnabled, List<Guid> RoleIds);
public sealed record CreateUserCommand(string Email, string FullName, string Password, List<Guid>? RoleIds) : ICommand<Guid>;
public sealed record AssignRolesCommand(Guid UserId, List<Guid> RoleIds) : ICommand<Unit>;
public sealed record ListUsersQuery(int Page, int PageSize) : IQuery<PagedResult<UserDto>>;

internal sealed class CreateUserHandler(IIdentityDbContext db, IPasswordService pw) : ICommandHandler<CreateUserCommand, Guid>
{
    public async Task<Guid> Handle(CreateUserCommand c, CancellationToken ct)
    {
        var email = c.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new ConflictException("A user with this e-mail already exists.");
        if (c.Password.Length < 12) throw new DomainException("Password must be at least 12 characters.");
        var roleIds = c.RoleIds ?? [];
        if (roleIds.Count > 0 && await db.Roles.CountAsync(r => roleIds.Contains(r.Id), ct) != roleIds.Distinct().Count())
            throw new DomainException("One or more roles do not exist.");
        var user = new User(email, c.FullName, pw.Hash(c.Password));
        user.SetRoles(roleIds);
        db.Users.Add(user);
        return user.Id;
    }
}

internal sealed class AssignRolesHandler(IIdentityDbContext db) : ICommandHandler<AssignRolesCommand, Unit>
{
    public async Task<Unit> Handle(AssignRolesCommand c, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == c.UserId, ct) ?? throw new NotFoundException(nameof(User), c.UserId);
        var ids = c.RoleIds;
        if (ids.Count > 0 && await db.Roles.CountAsync(r => ids.Contains(r.Id), ct) != ids.Distinct().Count()) throw new DomainException("One or more roles do not exist.");
        user.SetRoles(ids);
        return Unit.Value;
    }
}

internal sealed class ListUsersHandler(IIdentityDbContext db) : IQueryHandler<ListUsersQuery, PagedResult<UserDto>>
{
    public async Task<PagedResult<UserDto>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 1, 200);
        var query = db.Users.AsNoTracking().OrderBy(u => u.Email);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size)
            .Select(u => new UserDto(u.Id, u.Email, u.FullName, u.IsActive, u.MfaEnabled, u.RoleIds)).ToListAsync(ct);
        return new PagedResult<UserDto>(items, page, size, total);
    }
}

public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, List<string> Permissions);
public sealed record CreateRoleCommand(string Name, string? Description, List<string> Permissions) : ICommand<Guid>;
public sealed record ListRolesQuery() : IQuery<List<RoleDto>>;

internal sealed class CreateRoleHandler(IIdentityDbContext db) : ICommandHandler<CreateRoleCommand, Guid>
{
    public async Task<Guid> Handle(CreateRoleCommand c, CancellationToken ct)
    {
        var unknown = c.Permissions.Where(p => p != "*" && !Perm.All.Contains(p)).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown permissions: {string.Join(", ", unknown)}");
        var name = c.Name.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name, ct)) throw new ConflictException("Role name already exists.");
        var role = new Role(name, c.Description, c.Permissions);
        db.Roles.Add(role);
        return role.Id;
    }
}

internal sealed class ListRolesHandler(IIdentityDbContext db) : IQueryHandler<ListRolesQuery, List<RoleDto>>
{
    public Task<List<RoleDto>> Handle(ListRolesQuery q, CancellationToken ct) =>
        db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem, r.Permissions)).ToListAsync(ct);
}

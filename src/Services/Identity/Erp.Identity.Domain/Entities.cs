namespace Erp.Identity.Domain;

public sealed class Tenant : AuditableEntity
{
    private Tenant() { }
    public Tenant(string code, string name, string defaultCurrency, string defaultCulture)
    {
        Code = Guard.NotBlank(code, nameof(code)).ToLowerInvariant();
        Name = Guard.NotBlank(name, nameof(name));
        DefaultCurrency = defaultCurrency.ToUpperInvariant();
        DefaultCulture = defaultCulture;
        IsActive = true;
        Raise(new TenantRegisteredEvent(Id, Code, Name));
    }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string DefaultCurrency { get; private set; } = "USD";
    public string DefaultCulture { get; private set; } = "en";
    public bool IsActive { get; private set; }
    public void Deactivate() => IsActive = false;
}

public sealed class Role : TenantEntity
{
    private Role() { }
    public Role(string name, string? description, IEnumerable<string> permissions, bool isSystem = false)
    {
        Name = Guard.NotBlank(name, nameof(name)); Description = description; IsSystem = isSystem;
        Permissions = permissions.Distinct().ToList();
    }
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public bool IsSystem { get; private set; }
    public List<string> Permissions { get; private set; } = [];
}

public sealed class User : TenantEntity
{
    private User() { }
    public User(string email, string fullName, string passwordHash)
    {
        Email = Guard.NotBlank(email, nameof(email)).ToLowerInvariant();
        FullName = Guard.NotBlank(fullName, nameof(fullName));
        PasswordHash = passwordHash; IsActive = true;
    }
    public string Email { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public bool IsActive { get; private set; }
    public string Culture { get; private set; } = "en";
    public bool MfaEnabled { get; private set; }
    public string? MfaSecret { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public List<Guid> RoleIds { get; private set; } = [];

    public bool IsLockedOut(DateTime now) => LockoutEndUtc.HasValue && LockoutEndUtc > now;
    public void RegisterFailedLogin(DateTime now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= 5) { LockoutEndUtc = now.AddMinutes(15); FailedLoginCount = 0; }
    }
    public void RegisterSuccessfulLogin() { FailedLoginCount = 0; LockoutEndUtc = null; }
    public void SetRoles(IEnumerable<Guid> roleIds) => RoleIds = roleIds.Distinct().ToList();
    public void BeginMfaSetup(string secret) { MfaSecret = secret; MfaEnabled = false; }
    public void EnableMfa() { if (MfaSecret is null) throw new DomainException("MFA setup was not started."); MfaEnabled = true; }
    public void Deactivate() => IsActive = false;
}

public sealed class RefreshToken : TenantEntity
{
    private RefreshToken() { }
    public RefreshToken(Guid userId, string tokenHash, DateTime expiresAtUtc) { UserId = userId; TokenHash = tokenHash; ExpiresAtUtc = expiresAtUtc; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public void Revoke() => RevokedAtUtc ??= DateTime.UtcNow;
}

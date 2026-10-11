namespace Erp.Identity.Application;

public sealed record LoginCommand(string TenantCode, string Email, string Password, string? MfaCode) : ICommand<LoginResult>;

internal sealed class LoginHandler(IIdentityDbContext db, ITenantContext tenantCtx, IPasswordService pw, ITotpService totp, TokenIssuer issuer)
    : ICommandHandler<LoginCommand, LoginResult>
{
    public async Task<LoginResult> Handle(LoginCommand c, CancellationToken ct)
    {
        var code = c.TenantCode.Trim().ToLowerInvariant();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Code == code && t.IsActive, ct);
        if (tenant is null) return new(LoginStatus.InvalidCredentials);
        tenantCtx.Set(tenant.Id);

        var email = c.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !user.IsActive) return new(LoginStatus.InvalidCredentials);

        var now = DateTime.UtcNow;
        if (user.IsLockedOut(now)) return new(LoginStatus.LockedOut);
        if (!pw.Verify(user.PasswordHash, c.Password)) { user.RegisterFailedLogin(now); return new(LoginStatus.InvalidCredentials); }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(c.MfaCode)) return new(LoginStatus.MfaRequired);
            if (!totp.Verify(user.MfaSecret!, c.MfaCode)) { user.RegisterFailedLogin(now); return new(LoginStatus.InvalidCredentials); }
        }
        user.RegisterSuccessfulLogin();
        return await issuer.IssueAsync(user, ct);
    }
}

public sealed record RefreshCommand(string RefreshToken) : ICommand<LoginResult>;

internal sealed class RefreshHandler(IIdentityDbContext db, ITenantContext tenantCtx, ITokenService tokens, TokenIssuer issuer)
    : ICommandHandler<RefreshCommand, LoginResult>
{
    public async Task<LoginResult> Handle(RefreshCommand c, CancellationToken ct)
    {
        var parts = c.RefreshToken.Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tenantId)) return new(LoginStatus.InvalidCredentials);
        tenantCtx.Set(tenantId);

        var hash = tokens.Hash(c.RefreshToken);
        var rt = await db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (rt is null) return new(LoginStatus.InvalidCredentials);
        if (rt.RevokedAtUtc != null)   // reuse of a rotated token => assume theft, revoke the whole family
        {
            var userId = rt.UserId;
            foreach (var t in await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAtUtc == null).ToListAsync(ct)) t.Revoke();
            return new(LoginStatus.InvalidCredentials);
        }
        if (rt.ExpiresAtUtc < DateTime.UtcNow) return new(LoginStatus.InvalidCredentials);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == rt.UserId && u.IsActive, ct);
        if (user is null) return new(LoginStatus.InvalidCredentials);
        rt.Revoke();
        return await issuer.IssueAsync(user, ct);
    }
}

public sealed record MfaSetupResult(string Secret, string OtpAuthUri);
public sealed record SetupMfaCommand() : ICommand<MfaSetupResult>;
public sealed record EnableMfaCommand(string Code) : ICommand<bool>;

internal sealed class SetupMfaHandler(IIdentityDbContext db, ICurrentUser me, ITotpService totp) : ICommandHandler<SetupMfaCommand, MfaSetupResult>
{
    public async Task<MfaSetupResult> Handle(SetupMfaCommand c, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me.UserId, ct) ?? throw new NotFoundException(nameof(User), me.UserId!);
        var secret = totp.GenerateSecret();
        user.BeginMfaSetup(secret);
        return new MfaSetupResult(secret, totp.BuildUri("ERP", user.Email, secret));
    }
}

internal sealed class EnableMfaHandler(IIdentityDbContext db, ICurrentUser me, ITotpService totp) : ICommandHandler<EnableMfaCommand, bool>
{
    public async Task<bool> Handle(EnableMfaCommand c, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me.UserId, ct) ?? throw new NotFoundException(nameof(User), me.UserId!);
        if (user.MfaSecret is null || !totp.Verify(user.MfaSecret, c.Code)) throw new DomainException("Invalid verification code.");
        user.EnableMfa();
        return true;
    }
}

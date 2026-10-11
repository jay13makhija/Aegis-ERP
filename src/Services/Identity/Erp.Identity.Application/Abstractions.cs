using System.Security.Cryptography;
using Microsoft.Extensions.Options;
namespace Erp.Identity.Application;

public interface IIdentityDbContext : IUnitOfWork
{
    DbSet<Tenant> Tenants { get; }
    DbSet<User> Users { get; }
    DbSet<Role> Roles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
}
public interface IPasswordService { string Hash(string password); bool Verify(string hash, string password); }
public interface ITotpService
{
    string GenerateSecret();
    string BuildUri(string issuer, string account, string secret);
    bool Verify(string secret, string code);
}
public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);
public interface ITokenService
{
    IssuedToken CreateAccessToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions);
    string Hash(string value);
}

public enum LoginStatus { Success, InvalidCredentials, MfaRequired, LockedOut }
public sealed record LoginResult(LoginStatus Status, string? AccessToken = null, string? RefreshToken = null, int ExpiresInSeconds = 0);

/// <summary>Builds the access token (roles + flattened permissions) and a rotating refresh token ("{tenantId}.{random}", stored hashed).</summary>
public sealed class TokenIssuer(IIdentityDbContext db, ITokenService tokens, IOptions<JwtOptions> jwt)
{
    public async Task<LoginResult> IssueAsync(User user, CancellationToken ct)
    {
        var roleIds = user.RoleIds;
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        var access = tokens.CreateAccessToken(user, roles.Select(r => r.Name), roles.SelectMany(r => r.Permissions).Distinct());
        var refresh = $"{user.TenantId:N}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))}";
        db.RefreshTokens.Add(new RefreshToken(user.Id, tokens.Hash(refresh), DateTime.UtcNow.AddDays(jwt.Value.RefreshTokenDays)));
        return new LoginResult(LoginStatus.Success, access.AccessToken, refresh, access.ExpiresInSeconds);
    }
}

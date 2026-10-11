using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Erp.BuildingBlocks.Infrastructure;

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; private set; }
    public bool HasTenant => TenantId != Guid.Empty;
    public void Set(Guid tenantId) => TenantId = tenantId;
}

/// <summary>Claims-backed user. Outside an HTTP request (message consumers, background jobs) the actor is "system".</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;
    public string UserName => IsAuthenticated ? (Principal!.FindFirstValue("name") ?? Principal.FindFirstValue("sub") ?? "unknown") : "system";
    public IReadOnlyCollection<string> Roles => Principal?.FindAll("role").Select(c => c.Value).ToArray() ?? [];
    public IReadOnlyCollection<string> Permissions => Principal?.FindAll("permission").Select(c => c.Value).ToArray() ?? [];
}

/// <summary>AES-256-GCM field-level encryption (data at rest, on top of database TDE). Key comes from configuration/KMS (Encryption:Key, base64, 32 bytes).</summary>
public static class FieldEncryption
{
    private static byte[]? _key;
    public static void Configure(string base64Key)
    {
        var k = Convert.FromBase64String(base64Key);
        if (k.Length != 32) throw new InvalidOperationException("Encryption:Key must be 32 bytes (base64).");
        _key = k;
    }

    public static string Encrypt(string plain)
    {
        var key = _key ?? throw new InvalidOperationException("FieldEncryption not configured.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string Decrypt(string value)
    {
        var key = _key ?? throw new InvalidOperationException("FieldEncryption not configured.");
        var all = Convert.FromBase64String(value);
        var nonce = all[..12]; var tag = all[12..28]; var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    public static PropertyBuilder<string?> IsEncrypted(this PropertyBuilder<string?> b)
        => b.HasConversion(v => v == null ? null : Encrypt(v), v => v == null ? null : Decrypt(v));
}

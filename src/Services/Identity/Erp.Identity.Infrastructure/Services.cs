using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
namespace Erp.Identity.Infrastructure;

public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<object> _hasher = new();   // PBKDF2-HMAC-SHA512, per-hash salt, versioned format
    public string Hash(string password) => _hasher.HashPassword(null!, password);
    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(null!, hash, password) is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
}

public sealed class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _o = options.Value;

    public IssuedToken CreateAccessToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()), new("tenant_id", user.TenantId.ToString()),
            new("name", user.FullName), new("email", user.Email), new("mfa", user.MfaEnabled ? "true" : "false")
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        var lifetime = TimeSpan.FromMinutes(_o.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims), Issuer = _o.Issuer, Audience = _o.Audience,
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey)), SecurityAlgorithms.HmacSha256)
        };
        return new IssuedToken(new JsonWebTokenHandler().CreateToken(descriptor), (int)lifetime.TotalSeconds);
    }

    public string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>RFC 6238 TOTP (SHA-1, 6 digits, 30 s step) compatible with Google/Microsoft Authenticator. Accepts ±1 step of clock drift.</summary>
public sealed class TotpService : ITotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public string BuildUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";

    public bool Verify(string secret, string code)
    {
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        var key = Base32Decode(secret);
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var w = -1; w <= 1; w++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, step + w)), Encoding.ASCII.GetBytes(code))) return true;
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        var msg = BitConverter.GetBytes(counter); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder(); int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string s)
    {
        var bytes = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var c in s.TrimEnd('=').ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c); if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }
}

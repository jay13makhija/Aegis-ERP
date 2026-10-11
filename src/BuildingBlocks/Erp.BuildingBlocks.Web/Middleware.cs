using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Erp.BuildingBlocks.Web;

/// <summary>Tenant comes from the JWT claim. Header X-Tenant-Id is honoured only for unauthenticated calls (login) and must match the claim otherwise.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, ITenantContext tenant)
    {
        var header = ctx.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (Guid.TryParse(ctx.User.FindFirstValue("tenant_id"), out var fromClaim))
        {
            if (Guid.TryParse(header, out var h) && h != fromClaim)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("Tenant mismatch.");
                return;
            }
            tenant.Set(fromClaim);
        }
        else if (Guid.TryParse(header, out var fromHeader)) tenant.Set(fromHeader);
        await next(ctx);
    }
}

/// <summary>Logs authenticated API access into the immutable audit trail (disable with Audit:LogAccess=false).</summary>
public sealed class AccessAuditMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, IEventBus bus, ITenantContext tenant, ICurrentUser user, ServiceInfo svc,
        IConfiguration cfg, ILogger<AccessAuditMiddleware> log)
    {
        await next(ctx);
        if (!cfg.GetValue("Audit:LogAccess", true) || !user.IsAuthenticated || !ctx.Request.Path.StartsWithSegments("/api")) return;
        try
        {
            var details = JsonSerializer.Serialize(new { ctx.Request.Method, Path = ctx.Request.Path.Value, Status = ctx.Response.StatusCode });
            await bus.PublishAsync(new AuditRecordedEvent(svc.Name, "HttpRequest", ctx.TraceIdentifier, "Access", details,
                user.UserId, user.UserName, ctx.Connection.RemoteIpAddress?.ToString()) { TenantId = tenant.TenantId });
        }
        catch (Exception ex) { log.LogWarning(ex, "Access audit event could not be published"); }
    }
}

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            NotFoundException => (404, "Not found"),
            DomainException => (422, "Business rule violation"),
            ForbiddenException => (403, "Forbidden"),
            ConflictException or DbUpdateException => (409, "Conflict"),
            UnauthorizedAccessException => (401, "Unauthorized"),
            _ => (500, "Server error")
        };
        if (status == 500) log.LogError(ex, "Unhandled exception");
        ctx.Response.StatusCode = status;
        var detail = status == 500 ? "An unexpected error occurred." : ex is DbUpdateException ? "The change conflicts with existing or concurrently modified data. Reload and retry." : ex.Message;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail }, ct);
        return true;
    }
}

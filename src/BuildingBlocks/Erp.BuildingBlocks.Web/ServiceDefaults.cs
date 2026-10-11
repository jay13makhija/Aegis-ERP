using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
namespace Erp.BuildingBlocks.Web;

public static class ServiceDefaults
{
    /// <summary>One call wires a module: DB, tenancy, CQRS, outbox/inbox + bus, JWT + permission RBAC, OpenAPI, health, rate limiting.</summary>
    public static WebApplicationBuilder AddErpService<TContext>(this WebApplicationBuilder builder, string serviceName, Assembly applicationAssembly)
        where TContext : ErpDbContext
    {
        var cfg = builder.Configuration;
        var s = builder.Services;

        s.AddSingleton(new ServiceInfo(serviceName));
        s.AddHttpContextAccessor();
        s.AddScoped<ITenantContext, TenantContext>();
        s.AddScoped<ICurrentUser, CurrentUser>();

        FieldEncryption.Configure(cfg["Encryption:Key"] ?? throw new InvalidOperationException("Encryption:Key is missing."));

        s.AddDbContext<TContext>(o => o.UseSqlServer(cfg.GetConnectionString("Default"), sql => sql.EnableRetryOnFailure()));
        s.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TContext>());
        s.AddApplication(applicationAssembly);
        s.AddMessaging<TContext>(cfg);          // must come after AddApplication

        // --- authentication / RBAC ---
        s.Configure<JwtOptions>(cfg.GetSection("Jwt"));
        var jwt = cfg.GetSection("Jwt").Get<JwtOptions>() ?? throw new InvalidOperationException("Jwt section is missing.");
        if (jwt.SigningKey.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
        s.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                ValidateAudience = true, ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name", RoleClaimType = "role"
            };
            o.Events = new JwtBearerEvents   // SignalR sends the token as a query string parameter
            {
                OnMessageReceived = c =>
                {
                    var token = c.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && c.HttpContext.Request.Path.StartsWithSegments("/hubs")) c.Token = token;
                    return Task.CompletedTask;
                }
            };
        });
        s.AddAuthorization();
        s.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        s.AddSingleton<IAuthorizationHandler, PermissionHandler>();

        // --- web plumbing ---
        s.AddProblemDetails();
        s.AddExceptionHandler<GlobalExceptionHandler>();
        s.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        s.AddOpenApi();
        s.AddHealthChecks().AddDbContextCheck<TContext>("database");
        s.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? [])
            .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            var permits = cfg.GetValue("RateLimit:PermitPerMinute", 600);
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.User.FindFirst("tenant_id")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return builder;
    }

    public static WebApplication UseErpService(this WebApplication app)
    {
        app.UseExceptionHandler();
        if (app.Configuration.GetValue("Security:RequireHttps", false)) { app.UseHsts(); app.UseHttpsRedirection(); }
        app.UseCors();
        app.UseAuthentication();
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseMiddleware<AccessAuditMiddleware>();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready");
        if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:Enabled", false)) app.MapOpenApi();
        return app;
    }
}

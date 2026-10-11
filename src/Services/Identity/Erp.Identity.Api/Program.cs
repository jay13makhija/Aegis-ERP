using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.AddErpService<IdentityDbContext>("identity", typeof(IIdentityDbContext).Assembly);
builder.Services.AddScoped<IIdentityDbContext>(sp => sp.GetRequiredService<IdentityDbContext>());
builder.Services.AddScoped<TokenIssuer>();
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<ITotpService, TotpService>();

var app = builder.Build();
await app.InitializeDatabaseAsync<IdentityDbContext>();
app.UseErpService();

// ---- authentication ----
var auth = app.MapGroup("/api/auth").WithTags("Auth");
auth.MapPost("/login", async (LoginCommand cmd, IDispatcher d, CancellationToken ct) =>
{
    var r = await d.Send(cmd, ct);
    return r.Status switch
    {
        LoginStatus.Success => Results.Ok(r),
        LoginStatus.MfaRequired => Results.Json(new { status = "MfaRequired" }, statusCode: 401),
        LoginStatus.LockedOut => Results.Json(new { status = "LockedOut" }, statusCode: 423),
        _ => Results.Json(new { status = "InvalidCredentials" }, statusCode: 401)
    };
}).AllowAnonymous();
auth.MapPost("/refresh", async (RefreshCommand cmd, IDispatcher d, CancellationToken ct) =>
{
    var r = await d.Send(cmd, ct);
    return r.Status == LoginStatus.Success ? Results.Ok(r) : Results.Unauthorized();
}).AllowAnonymous();
auth.MapPost("/mfa/setup", (IDispatcher d, CancellationToken ct) => d.Send(new SetupMfaCommand(), ct)).RequireAuthorization();
auth.MapPost("/mfa/enable", (EnableMfaCommand cmd, IDispatcher d, CancellationToken ct) => d.Send(cmd, ct)).RequireAuthorization();

// ---- tenant onboarding (platform operator only) ----
app.MapPost("/api/tenants", async (RegisterTenantCommand cmd, HttpRequest req, IConfiguration cfg, IDispatcher d, CancellationToken ct) =>
{
    var expected = cfg["Platform:ApiKey"] ?? "";
    var given = req.Headers["X-Platform-Key"].ToString();
    if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given)))
        return Results.Unauthorized();
    var t = await d.Send(cmd, ct);
    return Results.Created($"/api/tenants/{t.Id}", t);
}).AllowAnonymous().WithTags("Tenants");

// ---- users / roles ----
app.MapGet("/api/users", (int? page, int? pageSize, IDispatcher d, CancellationToken ct) => d.Query(new ListUsersQuery(page ?? 1, pageSize ?? 50), ct))
    .RequirePermission(Perm.UsersRead).WithTags("Users");
app.MapPost("/api/users", async (CreateUserCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/users", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.UsersWrite).WithTags("Users");
app.MapPut("/api/users/{id:guid}/roles", async (Guid id, List<Guid> roleIds, IDispatcher d, CancellationToken ct) =>
{ await d.Send(new AssignRolesCommand(id, roleIds), ct); return Results.NoContent(); }).RequirePermission(Perm.UsersWrite).WithTags("Users");
app.MapGet("/api/roles", (IDispatcher d, CancellationToken ct) => d.Query(new ListRolesQuery(), ct)).RequirePermission(Perm.RolesRead).WithTags("Roles");
app.MapPost("/api/roles", async (CreateRoleCommand cmd, IDispatcher d, CancellationToken ct) =>
    Results.Created("/api/roles", new { id = await d.Send(cmd, ct) })).RequirePermission(Perm.RolesWrite).WithTags("Roles");
app.MapGet("/api/permissions", () => Perm.All).RequirePermission(Perm.RolesRead).WithTags("Roles");

app.Run();

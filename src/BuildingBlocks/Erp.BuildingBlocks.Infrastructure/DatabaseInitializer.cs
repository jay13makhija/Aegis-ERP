using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Erp.BuildingBlocks.Infrastructure;

public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending EF Core migrations at start-up (Database:AutoMigrate, default true). Retries while SQL Server is still booting.
    /// If the service has no migrations yet it falls back to EnsureCreated so a fresh dev database still works.
    /// </summary>
    public static async Task InitializeDatabaseAsync<TContext>(this IHost host) where TContext : ErpDbContext
    {
        using var scope = host.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        if (!cfg.GetValue("Database:AutoMigrate", true)) return;
        var log = scope.ServiceProvider.GetRequiredService<ILogger<TContext>>();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
                else
                {
                    log.LogWarning("No EF migrations found for {Context}; using EnsureCreated. Run scripts/init-migrations to switch to migrations.", typeof(TContext).Name);
                    await db.Database.EnsureCreatedAsync();
                }
                return;
            }
            catch (Exception ex) when (attempt < 12)
            {
                log.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 5s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}

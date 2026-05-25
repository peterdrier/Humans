using Humans.Application.Architecture;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Applies pending EF migrations in StartingAsync — must complete before any other
/// hosted service's StartAsync (cache warmup etc. read tables that don't exist yet).
/// </summary>
[Grandfathered(
    ruleId: "HUM0009",
    justification: "Persistence-boundary bootstrap. The migration runner is part of HumansDbContext's wiring, not a consumer of it — it cannot route through a repository because it operates on the schema itself. Follow-up to teach the analyzer about hosted-service / design-time-factory roles in #750.",
    since: "2026-05-17",
    issueRef: "nobodies-collective/Humans#750")]
internal sealed class DatabaseMigrationHostedService(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    : IHostedLifecycleService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DatabaseMigration");

    // Preserve the "DatabaseMigration" log category for existing dashboards.

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var dbName = dbContext.Database.GetDbConnection().Database;
        await MigrateAsync(dbContext, dbName, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateAsync(HumansDbContext dbContext, string dbName, CancellationToken cancellationToken)
    {
        try
        {
            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();

            // Warning level (not Information) so the per-boot migration breadcrumb survives
            // production's default log filtering — makes "when did migrations run?" answerable.
            _logger.LogWarning(
                "Database {Database}: {AppliedCount} applied migrations, {PendingCount} pending",
                dbName, applied.Count, pending.Count);

            if (pending.Count > 0)
            {
                foreach (var migration in pending)
                {
                    _logger.LogWarning("Applying pending migration: {Migration}", migration);
                }

                await dbContext.Database.MigrateAsync(cancellationToken);

                var nowApplied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
                _logger.LogWarning(
                    "Database {Database}: migrations complete — {AppliedCount} total applied",
                    dbName, nowApplied.Count);
            }
            else
            {
                _logger.LogInformation("Database {Database}: schema is up to date", dbName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Database migration failed for {Database}. The application may not function correctly",
                dbName);
            throw;
        }
    }
}

using Humans.Application.Architecture;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Applies pending EF Core migrations at startup, blocking application boot
/// until the schema is current. Implements <see cref="IHostedLifecycleService"/>
/// so migration runs in <c>StartingAsync</c> — strictly before any
/// <c>IHostedService.StartAsync</c> fires. This ordering matters: other hosted
/// services (cache warmup, settings preload) read from tables that won't exist
/// until migrations complete.
/// </summary>
/// <remarks>
/// Uses <see cref="IServiceScopeFactory"/> to mirror the prior inline
/// migration block's resolution path in Program.cs — scoped
/// <see cref="HumansDbContext"/>, not the singleton factory. Failures rethrow
/// — boot must abort.
/// </remarks>
[Grandfathered(
    ruleId: "HUM0009",
    justification: "Persistence-boundary bootstrap. The migration runner is part of HumansDbContext's wiring, not a consumer of it — it cannot route through a repository because it operates on the schema itself. Follow-up to teach the analyzer about hosted-service / design-time-factory roles in #750.",
    since: "2026-05-17",
    issueRef: "nobodies-collective/Humans#750")]
internal sealed class DatabaseMigrationHostedService : IHostedLifecycleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public DatabaseMigrationHostedService(
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        // Preserve the "DatabaseMigration" log category from the previous
        // inline migration block in Program.cs so log shipping / dashboards
        // built on that category name keep working.
        _logger = loggerFactory.CreateLogger("DatabaseMigration");
    }

    // StartingAsync runs sequentially before ANY hosted service's StartAsync.
    // That ordering is the whole point — other hosted services (e.g. the
    // agent-settings warmup) query tables that won't exist until migrations
    // complete.
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
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

            _logger.LogInformation(
                "Database {Database}: {AppliedCount} applied migrations, {PendingCount} pending",
                dbName, applied.Count, pending.Count);

            if (pending.Count > 0)
            {
                foreach (var migration in pending)
                {
                    _logger.LogInformation("Pending migration: {Migration}", migration);
                }

                await dbContext.Database.MigrateAsync(cancellationToken);

                var nowApplied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
                _logger.LogInformation(
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

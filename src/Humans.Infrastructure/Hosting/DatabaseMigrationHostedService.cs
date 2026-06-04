using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Applies pending EF migrations in <c>StartingAsync</c>, before cache warmup
/// and other hosted services read tables that may not exist yet.
/// </summary>
internal sealed class DatabaseMigrationHostedService(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    : IHostedLifecycleService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DatabaseMigration");

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

            // Warning level so the per-boot migration breadcrumb survives
            // production's default log filtering.
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
                    "Database {Database}: migrations complete - {AppliedCount} total applied",
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

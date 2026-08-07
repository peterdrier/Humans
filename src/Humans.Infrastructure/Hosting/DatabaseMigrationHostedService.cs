using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Applies pending EF migrations in <c>StartingAsync</c>, before cache warmup
/// and other hosted services read tables that may not exist yet.
/// <c>HumansDbContext</c> migrates first (its historical chain provisions the
/// whole schema on fresh databases), then each per-section context registered
/// via <c>AddSectionDbContext</c> runs through <see cref="SectionMigrationRunner"/>
/// (nobodies-collective/Humans#858).
/// Immediately before the first schema change of the boot — whichever context
/// causes it — a <see cref="PreMigrationSnapshot"/> is taken, so no deploy can
/// change the schema without leaving a recoverable point behind
/// (nobodies-collective/Humans#845).
/// </summary>
internal sealed class DatabaseMigrationHostedService(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IEnumerable<SectionDbContextRegistration> sectionContexts,
    IConfiguration configuration,
    IHostEnvironment environment)
    : IHostedLifecycleService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DatabaseMigration");

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var dbName = dbContext.Database.GetDbConnection().Database;
        var sectionContextInstances = sectionContexts
            .Select(section => (
                section, Context: (DbContext)scope.ServiceProvider.GetRequiredService(section.ContextType)))
            .ToList();
        var snapshot = CreateSnapshot();

        Func<CancellationToken, Task> beforeSchemaChange;
        if (snapshot is null)
        {
            beforeSchemaChange = static _ => Task.CompletedTask;
        }
        else
        {
            // Collected once, before any context has applied anything, so the marker records the
            // frontier the whole deploy needs to clear - not just whichever context happens to
            // trigger the dump (nobodies-collective/Humans#989).
            var pendingFrontier = await CollectPendingFrontierAsync(
                dbContext, sectionContextInstances.Select(instance => instance.Context), cancellationToken);
            beforeSchemaChange = ct => snapshot.EnsureCapturedAsync(pendingFrontier, ct);
        }

        await MigrateAsync(dbContext, dbName, beforeSchemaChange, cancellationToken);

        foreach (var (section, sectionContext) in sectionContextInstances)
        {
            await SectionMigrationRunner.MigrateAsync(
                sectionContext, section.SentinelTable, _logger, beforeSchemaChange, cancellationToken);
        }

        // Reached only if every context migrated, so this is what tells the next boot that the
        // snapshot above is history rather than the rollback point for an unfinished deploy.
        snapshot?.MarkMigrationsComplete();
    }

    /// <summary>
    /// Every migration pending across <paramref name="dbContext"/> and every section context,
    /// qualified by context type so identically-timestamped IDs from different contexts can never
    /// collide. A dry read via <c>GetPendingMigrationsAsync</c> per context - nothing here applies
    /// anything (nobodies-collective/Humans#989).
    /// </summary>
    private static async Task<List<string>> CollectPendingFrontierAsync(
        HumansDbContext dbContext, IEnumerable<DbContext> sectionContexts, CancellationToken cancellationToken)
    {
        var frontier = new List<string>();
        foreach (var context in sectionContexts.Prepend((DbContext)dbContext))
        {
            var contextName = context.GetType().Name;
            var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
            frontier.AddRange(pending.Select(migration => $"{contextName}:{migration}"));
        }

        return frontier;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Builds the snapshot this boot dumps through, or <see langword="null"/> where snapshots do
    /// not apply. Only the deployed environments snapshot: Development and the integration-test
    /// host ("Testing") run against disposable local databases and have no <c>pg_dump</c>
    /// alongside them, so there is nothing to protect and nothing to dump with.
    /// </summary>
    private PreMigrationSnapshot? CreateSnapshot()
    {
        if (!environment.IsProduction() && !environment.IsStaging())
        {
            _logger.LogDebug(
                "Pre-migration snapshot disabled: environment is {Environment}",
                environment.EnvironmentName);
            return null;
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        return new PreMigrationSnapshot(connectionString, _logger);
    }

    private async Task MigrateAsync(
        HumansDbContext dbContext,
        string dbName,
        Func<CancellationToken, Task> beforeSchemaChange,
        CancellationToken cancellationToken)
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

                await beforeSchemaChange(cancellationToken);
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

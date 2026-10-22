using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Base.Hosting;

/// <summary>
/// Applies pending EF migrations in <c>StartingAsync</c>, before cache warmup and other hosted services read tables
/// that may not exist yet, running each context through <see cref="SectionMigrationRunner"/> in registration order.
/// A <see cref="PreMigrationSnapshot"/> is taken immediately before the boot's first schema change, so no deploy can
/// change the schema without leaving a recoverable point behind.
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
            // Collected once, before any context applies anything, so the marker records the frontier the whole deploy
            // must clear, not just the context that happens to trigger the dump.
            var pendingFrontier = await CollectPendingFrontierAsync(
                sectionContextInstances.Select(instance => instance.Context), cancellationToken);
            beforeSchemaChange = ct => snapshot.EnsureCapturedAsync(pendingFrontier, ct);
        }

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
    /// Every migration pending across every section context, qualified by context type so identically-timestamped IDs
    /// from different contexts never collide. A dry read: nothing here applies anything.
    /// </summary>
    internal static async Task<List<string>> CollectPendingFrontierAsync(
        IEnumerable<DbContext> sectionContexts, CancellationToken cancellationToken)
    {
        var frontier = new List<string>();
        foreach (var context in sectionContexts)
        {
            var contextName = context.GetType().Name;

            // GetPendingMigrationsAsync logs an Error when the history table doesn't exist yet (a new section's first
            // boot); then every migration the context knows is pending by definition, so skip the read.
            var historyTable = SectionMigrationsHistory.TableFor(context.GetType());
            var pending = await SectionMigrationRunner.TableExistsAsync(context, historyTable, cancellationToken)
                ? await context.Database.GetPendingMigrationsAsync(cancellationToken)
                : context.Database.GetMigrations();
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

}

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Migrates a per-section DbContext with real-up baseline detection
/// (nobodies-collective/Humans#858).
/// </summary>
/// <remarks>
/// <para>
/// Each section context carries a baseline migration containing the full
/// <c>CreateTable</c>/index/FK operations for its tables. While the historical
/// <c>HumansDbContext</c> chain remains intact, that chain still creates every
/// section's tables, so on all real databases (fresh and existing alike) the
/// section's tables already exist by the time the section context migrates —
/// executing the baseline would fail. This runner decides per context:
/// </para>
/// <list type="bullet">
/// <item>Section history table has rows → plain <c>MigrateAsync</c> (no-op or
/// pending post-baseline migrations).</item>
/// <item>History empty and the sentinel table exists → record the baseline as
/// applied WITHOUT executing it (using EF's own <see cref="IHistoryRepository"/>
/// script generation — the same mechanism <c>MigrateAsync</c> uses to record
/// migrations), then <c>MigrateAsync</c> for anything after the baseline.</item>
/// <item>History empty and the sentinel table absent (genuinely fresh database
/// that the historical chain does not provision, e.g. a section context running
/// in isolation, or any fresh database after the future history shrink) →
/// plain <c>MigrateAsync</c>, which executes the baseline for real.</item>
/// </list>
/// <para>
/// Idempotent by construction: once the baseline history row exists the first
/// branch is taken forever. Only the baseline (the earliest migration of the
/// section context) is ever fake-applied; later section migrations always run.
/// </para>
/// </remarks>
internal static class SectionMigrationRunner
{
    public static async Task MigrateAsync(DbContext db, string sentinelTable, ILogger logger, CancellationToken ct)
    {
        var contextName = db.GetType().Name;
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

        if (applied.Count == 0 && await SentinelTableExistsAsync(db, sentinelTable, ct))
        {
            var baselineId = db.Database.GetMigrations().First();
            logger.LogWarning(
                "{Context}: tables exist but history is empty - recording baseline {Baseline} as applied without executing",
                contextName, baselineId);
            await RecordBaselineAsAppliedAsync(db, baselineId, ct);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            foreach (var migration in pending)
            {
                logger.LogWarning("{Context}: applying pending migration: {Migration}", contextName, migration);
            }

            await db.Database.MigrateAsync(ct);
        }
        else
        {
            logger.LogInformation("{Context}: schema is up to date", contextName);
        }
    }

    private static async Task<bool> SentinelTableExistsAsync(DbContext db, string sentinelTable, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT to_regclass(@table) IS NOT NULL";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@table";
            parameter.Value = "public." + sentinelTable;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(ct);
            return result is true;
        }
    }

    private static async Task RecordBaselineAsAppliedAsync(DbContext db, string baselineId, CancellationToken ct)
    {
        var historyRepository = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(historyRepository.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            historyRepository.GetInsertScript(new HistoryRow(baselineId, EfProductVersion)), ct);
    }

    /// <summary>
    /// Mirrors what EF writes to the ProductVersion history column (its assembly
    /// informational version, without build metadata) so fake-applied baseline rows
    /// are indistinguishable from genuinely-applied ones.
    /// </summary>
    private static string EfProductVersion
    {
        get
        {
            var informational = typeof(Migration).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var metadataIndex = informational.IndexOf('+', StringComparison.Ordinal);
            return metadataIndex < 0 ? informational : informational[..metadataIndex];
        }
    }
}

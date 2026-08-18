using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Humans.Base.Hosting;

/// <summary>
/// Migrates a per-section DbContext with real-up baseline detection
/// (nobodies-collective/Humans#858).
/// </summary>
/// <remarks>
/// <para>
/// Each section context carries a baseline migration containing the full
/// <c>CreateTable</c>/index/FK operations for its tables. On databases
/// provisioned by the historical root (<c>HumansDbContext</c>) chain — deleted
/// at #858 peel 15 — the section's tables already exist by the time the section
/// context migrates, so executing the baseline would fail. This runner decides
/// per context:
/// </para>
/// <list type="bullet">
/// <item>Section history table has rows → plain <c>MigrateAsync</c> (no-op or
/// pending post-baseline migrations).</item>
/// <item>History empty and the sentinel table exists → record the baseline as
/// applied WITHOUT executing it (using EF's own <see cref="IHistoryRepository"/>
/// script generation — the same mechanism <c>MigrateAsync</c> uses to record
/// migrations), then <c>MigrateAsync</c> for anything after the baseline.</item>
/// <item>History empty and the sentinel table absent (genuinely fresh database —
/// with the root chain deleted, every fresh database is provisioned this way) →
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
    /// <summary>
    /// Brings one section context up to date, choosing between the three baseline branches
    /// described on the class, and applies whatever migrations remain after that decision.
    /// </summary>
    /// <param name="beforeSchemaChange">
    /// Invoked immediately before anything writes to the schema — including the baseline
    /// bookkeeping, which creates the section's history table (nobodies-collective/Humans#845).
    /// Idempotent across contexts: one snapshot per boot, not one per section.
    /// </param>
    public static async Task MigrateAsync(
        DbContext db,
        string sentinelTable,
        ILogger logger,
        Func<CancellationToken, Task> beforeSchemaChange,
        CancellationToken ct)
    {
        var contextName = db.GetType().Name;
        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

            if (applied.Count == 0 && await SentinelTableExistsAsync(db, sentinelTable, ct))
            {
                var baselineId = db.Database.GetMigrations().First();
                logger.LogWarning(
                    "{Context}: tables exist but history is empty - recording baseline {Baseline} as applied without executing",
                    contextName, baselineId);
                await beforeSchemaChange(ct);
                await RecordBaselineAsAppliedAsync(db, baselineId, ct);
                applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            // Warning level so the per-boot migration breadcrumb survives production's
            // default log filtering (nobodies-collective/Humans#960).
            logger.LogWarning(
                "{Context}: {AppliedCount} applied migrations, {PendingCount} pending",
                contextName, applied.Count, pending.Count);

            if (pending.Count > 0)
            {
                foreach (var migration in pending)
                {
                    logger.LogWarning("{Context}: applying pending migration: {Migration}", contextName, migration);
                }

                await beforeSchemaChange(ct);
                await db.Database.MigrateAsync(ct);

                var nowApplied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                logger.LogWarning(
                    "{Context}: migrations complete - {AppliedCount} total applied",
                    contextName, nowApplied.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Section migration failed for {Context}. The application may not function correctly",
                contextName);
            throw;
        }
    }

    private static async Task<bool> SentinelTableExistsAsync(DbContext db, string sentinelTable, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Plain string comparison, not to_regclass: to_regclass parses its
                // argument as an identifier and lowercases unquoted names, so a
                // mixed-case sentinel like DataProtectionKeys would never match.
                command.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                    "WHERE table_schema = 'public' AND table_name = @table)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@table";
                parameter.Value = sentinelTable;
                command.Parameters.Add(parameter);
                var result = await command.ExecuteScalarAsync(ct);
                return result is true;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task RecordBaselineAsAppliedAsync(DbContext db, string baselineId, CancellationToken ct)
    {
        var historyRepository = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(historyRepository.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            historyRepository.GetInsertScript(new HistoryRow(baselineId, ProductInfo.GetVersion())), ct);
    }
}

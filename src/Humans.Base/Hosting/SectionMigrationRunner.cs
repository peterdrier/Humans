using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Humans.Base.Hosting;

/// <summary>Migrates a per-section DbContext with real-up baseline detection.</summary>
/// <remarks>
/// <para>
/// Each section context's baseline migration creates all its tables. On a database the deleted root
/// <c>HumansDbContext</c> chain provisioned they already exist, so executing the baseline would fail. Per context:
/// </para>
/// <list type="bullet">
/// <item>History has rows → plain <c>MigrateAsync</c>.</item>
/// <item>History empty, sentinel table exists → record the baseline as applied WITHOUT executing it (through EF's own
/// <see cref="IHistoryRepository"/> script), then <c>MigrateAsync</c> for anything after it.</item>
/// <item>History empty, sentinel absent (a fresh database) → plain <c>MigrateAsync</c>, executing the baseline.</item>
/// </list>
/// <para>
/// Idempotent: once the baseline row exists the first branch is taken forever. Only the baseline is ever
/// fake-applied.
/// </para>
/// </remarks>
internal static class SectionMigrationRunner
{
    /// <summary>
    /// Brings one section context up to date, choosing between the three baseline branches
    /// described on the class, and applies whatever migrations remain after that decision.
    /// </summary>
    /// <param name="db">The section context to migrate.</param>
    /// <param name="sentinelTable">The section’s baseline-presence table.</param>
    /// <param name="logger">Records migration decisions and failures.</param>
    /// <param name="ct">Cancels migration work.</param>
    /// <param name="beforeSchemaChange">
    /// Invoked immediately before anything writes to the schema, including the baseline bookkeeping that creates the
    /// history table. Idempotent across contexts: one snapshot per boot.
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
            var historyTable = SectionMigrationsHistory.TableFor(db.GetType());
            var historyTableExists = await TableExistsAsync(db, historyTable, ct);

            // EF's applied/pending reads SELECT from the history table and log an Error when it doesn't exist yet,
            // which reads like an outage on a new section's first boot. The information_schema probe above asks
            // without that failing SELECT, so skip the EF reads when it says no.
            var applied = historyTableExists
                ? (await db.Database.GetAppliedMigrationsAsync(ct)).ToList()
                : [];

            if (applied.Count == 0 && await TableExistsAsync(db, sentinelTable, ct))
            {
                var baselineId = db.Database.GetMigrations().First();
                logger.LogWarning(
                    "{Context}: tables exist but history is empty - recording baseline {Baseline} as applied without executing",
                    contextName, baselineId);
                await beforeSchemaChange(ct);
                await RecordBaselineAsAppliedAsync(db, baselineId, ct);
                applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
                historyTableExists = true;
            }

            var pending = historyTableExists
                ? (await db.Database.GetPendingMigrationsAsync(ct)).ToList()
                : db.Database.GetMigrations().ToList();

            // Warning level so the per-boot migration breadcrumb survives production's default log filtering.
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

    /// <summary>
    /// Whether <paramref name="tableName"/> exists in the <c>public</c> schema: an information_schema probe, never the
    /// EF history read, which logs an Error when the table is missing.
    /// </summary>
    internal static async Task<bool> TableExistsAsync(DbContext db, string tableName, CancellationToken ct)
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
                // mixed-case table name like DataProtectionKeys would never match.
                command.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                    "WHERE table_schema = 'public' AND table_name = @table)";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@table";
                parameter.Value = tableName;
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

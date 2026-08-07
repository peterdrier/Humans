using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Humans.Infrastructure.Hosting;

/// <summary>
/// Captures a <c>pg_dump</c> snapshot immediately before the first schema change of a boot,
/// so every schema-changing deploy is preceded by a recoverable point
/// (nobodies-collective/Humans#845).
/// </summary>
/// <remarks>
/// <para>
/// The startup migration path is the only thing committed to this repo that runs on every
/// deploy and knows whether that deploy changes the schema, so the snapshot hangs off it:
/// <see cref="DatabaseMigrationHostedService"/> hands this to every migration runner, and
/// the first runner that has something to apply triggers the dump. Deploys with no pending
/// migrations never dump.
/// </para>
/// <para>
/// A failed dump throws, aborting startup <em>before</em> any schema change is applied — the
/// database is left exactly as the previous release left it, so rolling the image back is a
/// complete recovery. That is strictly safer than the pre-existing failure mode, where a bad
/// migration crash-loops the single instance with the schema already half-changed.
/// </para>
/// <para>
/// A failed migration crash-loops the container, and every restart re-enters this class with
/// the schema already part-changed. The rollback point therefore belongs to the <em>deploy</em>,
/// not the boot: the snapshot earns an <see cref="UnfinishedSuffix"/> suffix once
/// <c>pg_dump</c> has exited successfully, and loses it once a boot gets all the way through its
/// migrations. While that suffix is present later boots carry the same file forward instead of
/// dumping the damage over it, so the one snapshot that predates the deploy survives the crash
/// loop.
/// </para>
/// <para>
/// These snapshots are a fast local rollback point, not the archive: Coolify's own scheduled
/// backups remain the off-host copy. Restore procedure for both:
/// <c>docs/database-restore-runbook.md</c>.
/// </para>
/// </remarks>
internal sealed class PreMigrationSnapshot(string connectionString, ILogger logger)
{
    /// <summary>
    /// Container path the deploy mounts a persistent volume at (see <c>docker-compose.yml</c>
    /// and the Coolify volume of the same name). Fixed rather than configurable: the path is
    /// ours on both sides of the mount, so a setting would only add a way to get it wrong.
    /// </summary>
    public const string SnapshotDirectory = "/app/db-snapshots";

    /// <summary>
    /// Marks a snapshot whose deploy has not finished migrating. Dropped by
    /// <see cref="MarkMigrationsComplete"/>; while it is there the file is this deploy's
    /// rollback point and is neither overwritten nor pruned.
    /// </summary>
    internal const string UnfinishedSuffix = ".unfinished";

    /// <summary>
    /// Marks the file <c>pg_dump</c> is writing into. A dump only earns
    /// <see cref="UnfinishedSuffix"/> once the process has exited successfully, so a failed or
    /// killed dump can never leave a truncated file that a later boot mistakes for a rollback
    /// point. Nothing reads these; the next dump attempt deletes whatever it finds.
    /// </summary>
    private const string WritingSuffix = ".writing";

    /// <summary>
    /// Newest snapshots kept; older ones are deleted after a successful dump. Bounds disk use
    /// on the single host — schema-changing deploys are the only thing that writes here.
    /// </summary>
    private const int RetainedSnapshots = 10;

    private readonly NpgsqlConnectionStringBuilder _connection = new(connectionString);

    private bool _attempted;

    /// <summary>
    /// Ensures this deploy has a snapshot taken before it changed anything. The first call of a
    /// boot dumps the database — unless an earlier boot of the same deploy already left an
    /// unfinished snapshot, which is carried forward instead. Later calls in the same boot are
    /// no-ops, so a deploy that migrates several contexts still snapshots exactly once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The dump could not be taken. Thrown so the caller aborts before migrating.
    /// </exception>
    public async Task EnsureCapturedAsync(CancellationToken cancellationToken)
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        var database = _connection.Database!;
        var dump = Path.Combine(
            SnapshotDirectory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{database}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.dump"));
        var path = dump + UnfinishedSuffix;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(SnapshotDirectory);

            var carried = FindUnfinishedSnapshot(SnapshotDirectory, database);
            if (carried is not null)
            {
                // Warning level for the same reason as the "written" line below: in a crash loop
                // this is the line that tells you which file is the real rollback point. The age
                // is what separates the case this exists for - a restart minutes after the
                // failed deploy - from a marker left behind by an older one, which would make
                // this deploy's rollback point far older than it should be.
                logger.LogWarning(
                    "Reusing pre-migration snapshot {Path}, taken {AgeHours:F1}h ago: an earlier boot of " +
                    "this deploy took it and did not finish migrating, so it - not the current schema - " +
                    "is the rollback point. An age beyond this deploy means a stale marker; see " +
                    "docs/database-restore-runbook.md §5",
                    carried,
                    (DateTime.UtcNow - File.GetLastWriteTimeUtc(carried)).TotalHours);
                return;
            }

            DiscardAbandonedWrites(SnapshotDirectory, database);

            // Dump into a name nothing looks for, and only rename once pg_dump has exited 0.
            // Writing straight to the final name would let a dump that failed or was killed
            // half-way leave a truncated file that the next boot carries forward as this
            // deploy's rollback point - and then migrates on the strength of it.
            await RunPgDumpAsync(_connection, dump + WritingSuffix, cancellationToken);
            File.Move(dump + WritingSuffix, path, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Pre-migration snapshot of '{database}' failed, so pending migrations " +
                "were not applied and the schema is unchanged. Roll the image back, fix the " +
                "snapshot path or the pg_dump client, and redeploy. See docs/database-restore-runbook.md.",
                ex);
        }

        stopwatch.Stop();

        // Warning level so the snapshot breadcrumb survives production's default log filtering —
        // at 3am this log line is how you find the file.
        logger.LogWarning(
            "Pre-migration snapshot written: {Path} ({Bytes} bytes) in {ElapsedMs}ms",
            path, new FileInfo(path).Length, stopwatch.ElapsedMilliseconds);

        Prune(database);
    }

    /// <summary>
    /// Records that this boot got through its migrations, dropping the
    /// <see cref="UnfinishedSuffix"/> from any snapshot still carrying it. Run on every boot,
    /// including boots with nothing pending — that is what clears the marker after an operator
    /// has recovered a failed deploy by rolling the image back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws: the schema is already migrated by the time this runs, so bookkeeping must
    /// not be what fails the boot.
    /// </para>
    /// <para>
    /// A failure is logged at Error because of what it costs later: the marker stays, and the
    /// next schema-changing deploy carries this snapshot forward instead of dumping, leaving its
    /// rollback point older than the deploy it is meant to undo. It mostly self-heals — this runs
    /// on every boot, pending migrations or not, so any later restart retires the marker — but a
    /// deploy landing first would inherit it. Making that impossible needs the snapshot to record
    /// which deploy took it: nobodies-collective/Humans#989.
    /// </para>
    /// </remarks>
    public void MarkMigrationsComplete()
    {
        try
        {
            foreach (var completed in PromoteUnfinishedSnapshots(SnapshotDirectory, _connection.Database!))
            {
                logger.LogInformation(
                    "Pre-migration snapshot {Path}: deploy completed, snapshot retained as history", completed);
            }
        }
        catch (IOException ex)
        {
            LogRetirementFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogRetirementFailure(ex);
        }
    }

    private void LogRetirementFailure(Exception ex) =>
        logger.LogError(
            ex,
            "Could not retire the unfinished pre-migration snapshot in {Directory}. This deploy's " +
            "migrations succeeded, but until the marker clears the next schema-changing deploy will " +
            "reuse that snapshot instead of taking its own. Rename it to drop the '{Suffix}' suffix, " +
            "or restart the app once - see docs/database-restore-runbook.md §5",
            SnapshotDirectory,
            UnfinishedSuffix);

    /// <summary>
    /// The snapshot an earlier boot of this deploy left behind, or <see langword="null"/> if the
    /// last deploy finished. Oldest first: if several ever pile up, the earliest is the one that
    /// predates the most.
    /// </summary>
    internal static string? FindUnfinishedSnapshot(string directory, string database) =>
        UnfinishedSnapshots(directory, database).FirstOrDefault();

    /// <summary>
    /// Renames every unfinished snapshot of the database to its final name and returns the new
    /// paths.
    /// </summary>
    internal static List<string> PromoteUnfinishedSnapshots(string directory, string database)
    {
        var promoted = new List<string>();
        foreach (var unfinished in UnfinishedSnapshots(directory, database))
        {
            var completed = unfinished[..^UnfinishedSuffix.Length];
            File.Move(unfinished, completed, overwrite: true);
            promoted.Add(completed);
        }

        return promoted;
    }

    private static List<string> UnfinishedSnapshots(string directory, string database) =>
        [.. Directory
            .GetFiles(directory, database + "-*.dump" + UnfinishedSuffix)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];

    /// <summary>
    /// Deletes the output of any dump that did not finish. Nothing can be mid-dump here — this
    /// runs at startup on the single instance, before this boot's own dump — so anything still
    /// carrying <see cref="WritingSuffix"/> is the wreckage of an earlier attempt.
    /// </summary>
    private static void DiscardAbandonedWrites(string directory, string database)
    {
        foreach (var abandoned in Directory.GetFiles(directory, database + "-*.dump" + WritingSuffix))
        {
            File.Delete(abandoned);
        }
    }

    private static async Task RunPgDumpAsync(
        NpgsqlConnectionStringBuilder connection,
        string path,
        CancellationToken cancellationToken)
    {
        // --format=custom is what pg_restore needs for selective/parallel restore, and it is
        // the format the runbook restores from.
        var startInfo = new ProcessStartInfo("pg_dump")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add("--file=" + path);
        startInfo.ArgumentList.Add("--host=" + connection.Host);
        startInfo.ArgumentList.Add(
            "--port=" + connection.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username=" + connection.Username);
        startInfo.ArgumentList.Add("--dbname=" + connection.Database);
        // PGPASSWORD on the child only — never on the command line, which is world-readable in ps.
        startInfo.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("pg_dump did not start.");

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg_dump exited {process.ExitCode}: {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Deletes all but the <see cref="RetainedSnapshots"/> newest snapshots of the given
    /// database, so the volume cannot fill up over a long series of schema-changing deploys.
    /// </summary>
    /// <remarks>
    /// Never throws: by the time this runs the snapshot exists, so a housekeeping failure must
    /// not be what blocks the deploy. Worst case the directory grows and someone tidies it.
    /// </remarks>
    private void Prune(string database)
    {
        try
        {
            // The timestamp in the file name sorts lexicographically in chronological order.
            // Only completed snapshots are retention candidates: a suffixed file is either this
            // deploy's rollback point or a dump in flight, and neither is ours to delete on a
            // count.
            var stale = new DirectoryInfo(SnapshotDirectory)
                .GetFiles(database + "-*.dump")
                .Where(file => file.Name.EndsWith(".dump", StringComparison.Ordinal))
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(RetainedSnapshots);

            foreach (var file in stale)
            {
                file.Delete();
                logger.LogInformation("Pruned old pre-migration snapshot {Path}", file.FullName);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not prune old pre-migration snapshots in {Directory}", SnapshotDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not prune old pre-migration snapshots in {Directory}", SnapshotDirectory);
        }
    }
}

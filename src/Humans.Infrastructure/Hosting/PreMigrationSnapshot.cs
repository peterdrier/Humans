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
/// not the boot: the snapshot is written with an <see cref="UnfinishedSuffix"/> suffix and only
/// loses it once a boot gets all the way through its migrations. While that suffix is present
/// later boots carry the same file forward instead of dumping the damage over it, so the one
/// snapshot that predates the deploy survives the crash loop.
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
        var path = Path.Combine(
            SnapshotDirectory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{database}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.dump{UnfinishedSuffix}"));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(SnapshotDirectory);

            var carried = FindUnfinishedSnapshot(SnapshotDirectory, database);
            if (carried is not null)
            {
                // Warning level for the same reason as the "written" line below: in a crash loop
                // this is the line that tells you which file is the real rollback point.
                logger.LogWarning(
                    "Reusing pre-migration snapshot {Path}: an earlier boot of this deploy took it and " +
                    "did not finish migrating, so it - not the current schema - is the rollback point",
                    carried);
                return;
            }

            await RunPgDumpAsync(_connection, path, cancellationToken);
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
    /// Never throws: the schema is already migrated by the time this runs, so bookkeeping must
    /// not be what fails the boot. A failure leaves the marker in place, which costs the next
    /// deploy a fresh snapshot but never costs anyone the existing one.
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
            logger.LogWarning(ex, "Could not clear unfinished pre-migration snapshots in {Directory}", SnapshotDirectory);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Could not clear unfinished pre-migration snapshots in {Directory}", SnapshotDirectory);
        }
    }

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
            // Unfinished snapshots are excluded outright rather than left to retention: one of
            // them is the snapshot this call just wrote, and any other belongs to a deploy that
            // has not been recovered yet.
            var stale = new DirectoryInfo(SnapshotDirectory)
                .GetFiles(database + "-*.dump")
                .Where(file => !file.Name.EndsWith(UnfinishedSuffix, StringComparison.Ordinal))
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

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
    /// <see cref="MarkMigrationsComplete"/> when that deploy finishes, or by
    /// <see cref="EnsureCapturedAsync"/> if a later deploy finds it stale
    /// (nobodies-collective/Humans#989); while it is there the file is this deploy's rollback
    /// point and is neither overwritten nor pruned.
    /// </summary>
    internal const string UnfinishedSuffix = ".unfinished";

    /// <summary>
    /// Marks a file still being written — <c>pg_dump</c>'s output, or the
    /// <see cref="FrontierSuffix"/> sidecar in <see cref="WriteFrontier"/>. Neither earns the name
    /// a later boot looks for until its write has finished, so a write that failed or was killed
    /// half-way can never leave a truncated file that boot reads as authoritative. Nothing reads
    /// these; <see cref="DiscardAbandonedWrites"/> deletes whatever it finds.
    /// </summary>
    private const string WritingSuffix = ".writing";

    /// <summary>
    /// Sidecar recording the migrations that were pending, across every context, at the moment
    /// an <see cref="UnfinishedSuffix"/> snapshot was taken — its deploy identity
    /// (nobodies-collective/Humans#989). Read by <see cref="FrontierStillPending"/> to tell a
    /// marker a crash-loop is still carrying forward from one a completed deploy left behind.
    /// </summary>
    private const string FrontierSuffix = ".migrations";

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
    /// <param name="pendingMigrations">
    /// Every migration pending across every migrated context, collected once before any of them
    /// applies anything (nobodies-collective/Humans#989). Recorded alongside a fresh dump as its
    /// deploy identity, and compared against a carried-forward marker to tell a genuine
    /// crash-loop retry (some of it still pending) from a marker a completed deploy left behind
    /// (none of it still pending) — the marker's own migrations having finished is what makes it
    /// safe to retire instead of carrying it forward as a stale rollback point.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The dump could not be taken. Thrown so the caller aborts before migrating.
    /// </exception>
    public async Task EnsureCapturedAsync(
        IReadOnlyCollection<string> pendingMigrations, CancellationToken cancellationToken)
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

            var carried = CarryForwardMarker(SnapshotDirectory, database, pendingMigrations);
            if (carried is not null)
            {
                // Warning level for the same reason as the "written" line below: in a crash loop
                // this is the line that tells you which file is the real rollback point.
                logger.LogWarning(
                    "Reusing pre-migration snapshot {Path}, taken {AgeHours:F1}h ago: an earlier boot of " +
                    "this deploy took it and did not finish migrating, so it - not the current schema - " +
                    "is the rollback point. See docs/database-restore-runbook.md §5",
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

            // Outside the abort contract above, deliberately: the dump - the thing this deploy
            // must not migrate without - already exists. A sidecar that fails to write leaves the
            // marker with no recorded frontier, which FrontierStillPending reads as "still
            // pending": the unconditional carry-forward this had before #989, not a lost rollback
            // point. Not a reason to refuse the deploy.
            try
            {
                WriteFrontier(path, pendingMigrations);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    ex,
                    "Could not record the migration frontier for pre-migration snapshot {Path}. The dump " +
                    "itself is intact; without the sidecar a later deploy carries this marker forward " +
                    "unconditionally instead of retiring it once stale. See nobodies-collective/Humans#989",
                    path);
            }
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
    /// next schema-changing deploy finds it. It self-heals either way now
    /// (nobodies-collective/Humans#989) — a restart before that deploy retires the marker here,
    /// and the deploy itself retires it via <see cref="FrontierStillPending"/> in
    /// <see cref="EnsureCapturedAsync"/> once it sees none of the marker's recorded migrations
    /// are still pending. Logging stays at Error because between those two points the deploy's
    /// own rollback point is missing its dump.
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
            "find it. It self-heals - a restart retries this, or that deploy retires it itself once " +
            "none of its recorded migrations are still pending - but rename it to drop the " +
            "'{Suffix}' suffix, or restart the app once, to clear it sooner. See " +
            "docs/database-restore-runbook.md §5",
            SnapshotDirectory,
            UnfinishedSuffix);

    /// <summary>
    /// The snapshot an earlier boot of this deploy left behind and this boot must reuse instead of
    /// dumping, or <see langword="null"/> if there is none and this boot takes its own dump.
    /// Retires every stale marker it steps over on the way (nobodies-collective/Humans#989).
    /// </summary>
    /// <remarks>
    /// Walks all the markers, oldest first, rather than judging the oldest alone. Retiring a stale
    /// marker is best-effort, so a rename that failed leaves that marker sitting in front of one
    /// that is still live — and this boot's own marker is created behind it. Judging only the
    /// oldest would then retire the stale one, see nothing else, and dump the half-migrated schema
    /// as this deploy's rollback point, over a crash loop whose real rollback point was the marker
    /// it never looked at. Worse, a later boot promotes every marker, so that damaged dump becomes
    /// the newest completed snapshot — the one <c>docs/database-restore-runbook.md</c> restores.
    /// Oldest first is still what picks the winner among live markers: the earliest predates the
    /// most.
    /// </remarks>
    internal string? CarryForwardMarker(
        string directory, string database, IReadOnlyCollection<string> pendingMigrations)
    {
        foreach (var marker in UnfinishedSnapshots(directory, database))
        {
            if (FrontierStillPending(marker, pendingMigrations, logger))
            {
                return marker;
            }

            RetireStaleMarker(marker);
        }

        return null;
    }

    /// <summary>
    /// Retires a marker none of whose recorded migrations are still pending: the deploy that took
    /// it finished (nobodies-collective/Humans#989), so reusing it would make this deploy's
    /// rollback point predate the deploy before it. The same rename
    /// <see cref="MarkMigrationsComplete"/> does for a marker that clears normally.
    /// </summary>
    /// <remarks>
    /// Best-effort, unlike the dump itself: retiring a marker is bookkeeping, and the boot ends up
    /// with a rollback point either way. A rename that fails must not be what stops the boot — the
    /// marker stays, <see cref="CarryForwardMarker"/> steps over it next time, and a later boot
    /// retires it, the same tolerance <see cref="MarkMigrationsComplete"/> already gives the
    /// identical call.
    /// </remarks>
    private void RetireStaleMarker(string marker)
    {
        try
        {
            var retired = Retire(marker);
            logger.LogWarning(
                "Retired stale pre-migration snapshot {Path} as {Retired}: none of its recorded " +
                "migrations are still pending, so the deploy that took it already finished. See " +
                "nobodies-collective/Humans#989",
                marker, retired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex,
                "Could not retire stale pre-migration snapshot {Path}. The boot still gets a rollback " +
                "point - a live marker behind this one, or its own dump; the stale marker stays until a " +
                "boot clears it. See docs/database-restore-runbook.md §5",
                marker);
        }
    }

    /// <summary>
    /// Renames every unfinished snapshot of the database to its final name and returns the new
    /// paths.
    /// </summary>
    internal static List<string> PromoteUnfinishedSnapshots(string directory, string database) =>
        [.. UnfinishedSnapshots(directory, database).Select(Retire)];

    /// <summary>
    /// Renames one unfinished snapshot to its final name, retiring it from "this deploy's
    /// rollback point" to plain history, and drops its now-unneeded
    /// <see cref="FrontierSuffix"/> sidecar (nobodies-collective/Humans#989).
    /// </summary>
    private static string Retire(string unfinishedPath)
    {
        var completed = unfinishedPath[..^UnfinishedSuffix.Length];
        File.Move(unfinishedPath, completed, overwrite: true);
        File.Delete(unfinishedPath + FrontierSuffix);
        return completed;
    }

    /// <summary>
    /// Every snapshot of the database whose deploy has not finished migrating, oldest first.
    /// </summary>
    internal static List<string> UnfinishedSnapshots(string directory, string database) =>
        [.. Directory
            .GetFiles(directory, database + "-*.dump" + UnfinishedSuffix)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)];

    /// <summary>
    /// Records the migrations pending, across every context, when <paramref name="unfinishedPath"/>
    /// was taken — its deploy identity (nobodies-collective/Humans#989). One migration ID per
    /// line; nothing but <see cref="FrontierStillPending"/> reads it.
    /// </summary>
    /// <remarks>
    /// Published by rename, for the same reason the dump is: <see cref="File.WriteAllLines(string,
    /// IEnumerable{string})"/> truncates first, so a write that fails half-way — the snapshot
    /// volume filling up is the likely way — or a process killed mid-write would leave a
    /// <em>short</em> frontier. A frontier missing the migration that is in fact still pending is
    /// exactly what makes <see cref="FrontierStillPending"/> retire a marker it should have
    /// carried forward, so a partial sidecar is worse than none: readers must see the whole
    /// frontier or no file at all.
    /// </remarks>
    internal static void WriteFrontier(string unfinishedPath, IEnumerable<string> pendingMigrations)
    {
        var frontierPath = unfinishedPath + FrontierSuffix;
        File.WriteAllLines(frontierPath + WritingSuffix, pendingMigrations);
        File.Move(frontierPath + WritingSuffix, frontierPath, overwrite: true);
    }

    /// <summary>
    /// Whether any migration recorded as pending when <paramref name="unfinishedPath"/> was taken
    /// is still pending now. True carries the marker forward (a genuine crash-loop retry); false
    /// means the deploy that took it finished, so the marker is stale and safe to retire
    /// (nobodies-collective/Humans#989). A <see cref="FrontierSuffix"/> sidecar this cannot read —
    /// missing, because the snapshot was taken before this fix shipped, or unreadable through an
    /// IO or permission fault — fails safe as "still pending", the same unconditional
    /// carry-forward this had before the sidecar existed. Retiring a marker needs proof it is
    /// stale, and "I could not tell" is not proof; nor may a sidecar read be what aborts a boot,
    /// which is the one failure the deploy cannot recover from
    /// (<c>memory/architecture/no-startup-guards.md</c>).
    /// </summary>
    internal static bool FrontierStillPending(
        string unfinishedPath, IReadOnlyCollection<string> currentlyPendingMigrations, ILogger logger)
    {
        var frontierPath = unfinishedPath + FrontierSuffix;
        if (!File.Exists(frontierPath))
        {
            return true;
        }

        string[] frontier;
        try
        {
            frontier = File.ReadAllLines(frontierPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged at Error, unlike the expected missing-sidecar case above: the file is there
            // and the volume would not give it up, which is the same volume the dump writes to.
            logger.LogError(
                ex,
                "Could not read the recorded migration frontier {Path}. Carrying its snapshot forward as " +
                "this deploy's rollback point rather than retiring a marker that may still be live. See " +
                "docs/database-restore-runbook.md §5",
                frontierPath);
            return true;
        }

        return frontier.Any(currentlyPendingMigrations.Contains);
    }

    /// <summary>
    /// Deletes the output of any dump or frontier-sidecar write that did not finish. Nothing can
    /// be mid-write here — this runs at startup on the single instance, before this boot's own
    /// dump — so anything still carrying <see cref="WritingSuffix"/> is the wreckage of an earlier
    /// attempt.
    /// </summary>
    private static void DiscardAbandonedWrites(string directory, string database)
    {
        foreach (var abandoned in Directory.GetFiles(directory, database + "-*" + WritingSuffix))
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

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
    /// Newest snapshots kept; older ones are deleted after a successful dump. Bounds disk use
    /// on the single host — schema-changing deploys are the only thing that writes here.
    /// </summary>
    private const int RetainedSnapshots = 10;

    private bool _attempted;

    /// <summary>
    /// Dumps the database on the first call of a boot; later calls are no-ops so a deploy that
    /// migrates several contexts still produces exactly one snapshot, taken before the first
    /// schema change.
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

        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var path = Path.Combine(
            SnapshotDirectory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{connection.Database}-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.dump"));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(SnapshotDirectory);
            await RunPgDumpAsync(connection, path, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Pre-migration snapshot of '{connection.Database}' failed, so pending migrations " +
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

        Prune(connection.Database!);
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

    /// <remarks>
    /// Never throws: by the time this runs the snapshot exists, so a housekeeping failure must
    /// not be what blocks the deploy. Worst case the directory grows and someone tidies it.
    /// </remarks>
    private void Prune(string database)
    {
        try
        {
            // The timestamp in the file name sorts lexicographically in chronological order.
            var stale = new DirectoryInfo(SnapshotDirectory)
                .GetFiles(database + "-*.dump")
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

using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Debug.Services;

/// <summary>One registered data migration with what it still has to do.</summary>
internal sealed record DataMigrationReport(DataMigration Migration, DataMigrationStatus Status);

/// <summary>
/// Host for every section's <see cref="ISectionDataMigrations"/>: lists them, reads their
/// status, and applies one on an admin's confirm. The copy itself runs in the owning section's
/// delegate; this class only calls it with the request's services and writes the
/// <see cref="AuditAction.DataMigrationApplied"/> entry.
/// </summary>
internal sealed class DataMigrationService(
    IEnumerable<ISectionDataMigrations> contributions,
    IServiceProvider services,
    IAuditLogService audit,
    ILogger<DataMigrationService> logger)
{
    /// <summary>Every registered migration, ordered by section then key.</summary>
    public IReadOnlyList<DataMigration> All() =>
        [.. contributions
            .SelectMany(c => c.Migrations())
            .OrderBy(m => m.Section, StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal)];

    /// <summary>Every migration with its current status, pending rows included.</summary>
    public async Task<IReadOnlyList<DataMigrationReport>> GetReportsAsync(CancellationToken ct = default)
    {
        var reports = new List<DataMigrationReport>();
        foreach (var migration in All())
        {
            reports.Add(new DataMigrationReport(migration, await migration.Status(services, ct)));
        }
        return reports;
    }

    /// <summary>
    /// Runs the migration named <paramref name="key"/> for <paramref name="actorUserId"/> and
    /// audits the run. Returns the rows written, or <c>null</c> when no migration has that key.
    /// </summary>
    public async Task<int?> ApplyAsync(string key, Guid actorUserId, CancellationToken ct = default)
    {
        var migration = All().FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
        if (migration is null)
            return null;

        var rows = await migration.Apply(services, actorUserId, ct);

        await audit.LogAsync(
            AuditAction.DataMigrationApplied, "DataMigration", Guid.Empty,
            $"Data migration {migration.Key} ({migration.Section}) applied: {rows} row(s) written",
            actorUserId);
        logger.LogInformation(
            "Data migration {Key} applied by {ActorUserId}: {Rows} row(s) written",
            migration.Key, actorUserId, rows);

        return rows;
    }
}

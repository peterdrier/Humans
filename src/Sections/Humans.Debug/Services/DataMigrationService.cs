using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Debug.Services;

internal sealed record DataMigrationReport(DataMigration Migration, DataMigrationStatus Status);

/// <summary>
/// The copy runs in the owning section's delegate; this only calls it with the request's services and writes the
/// <see cref="DebugAuditActions.DataMigrationApplied"/> entry.
/// </summary>
internal sealed class DataMigrationService(
    IEnumerable<ISectionDataMigrations> contributions,
    IServiceProvider services,
    IAuditLogService audit,
    ILogger<DataMigrationService> logger)
{
    /// <summary>Ordered by section, then key.</summary>
    public IReadOnlyList<DataMigration> All() =>
        [.. contributions
            .SelectMany(c => c.Migrations())
            .OrderBy(m => m.Section, StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal)];

    public async Task<IReadOnlyList<DataMigrationReport>> GetReportsAsync(CancellationToken ct = default)
    {
        var reports = new List<DataMigrationReport>();
        foreach (var migration in All())
        {
            reports.Add(new DataMigrationReport(migration, await migration.Status(services, ct)));
        }
        return reports;
    }

    /// <summary>Returns the rows written, or <c>null</c> when no migration has that key.</summary>
    public async Task<int?> ApplyAsync(string key, Guid actorUserId, CancellationToken ct = default)
    {
        var migration = All().FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
        if (migration is null)
            return null;

        var rows = await migration.Apply(services, actorUserId, ct);

        await audit.LogAsync(
            DebugAuditActions.DataMigrationApplied, "DataMigration", Guid.Empty,
            $"Data migration {migration.Key} ({migration.Section}) applied: {rows} row(s) written",
            actorUserId);
        logger.LogInformation(
            "Data migration {Key} applied by {ActorUserId}: {Rows} row(s) written",
            migration.Key, actorUserId, rows);

        return rows;
    }
}

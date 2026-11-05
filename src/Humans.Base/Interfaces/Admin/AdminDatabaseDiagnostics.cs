using NodaTime;

namespace Humans.Base.Interfaces.Admin;

public interface IAdminDatabaseDiagnosticsService : IApplicationService
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);

    Task<DatabaseInfo> GetDatabaseInfoAsync(CancellationToken ct = default);
}

public sealed record DatabaseMigrationStatus(
    string? LastApplied,
    int AppliedCount,
    int PendingCount,
    IReadOnlyList<string> Applied,
    IReadOnlyList<SectionMigrationStatus> Sections);

/// <summary>
/// Migration state of one per-section DbContext, so QA/prod rollouts can confirm each baseline was recorded.
/// </summary>
public sealed record SectionMigrationStatus(
    string Context,
    string? LastApplied,
    int AppliedCount,
    int PendingCount);

/// <summary>What the database connection itself reports about the server and the current database.</summary>
public sealed record DatabaseInfo(
    DatabaseServerSummary Summary,
    IReadOnlyList<DatabaseTableInfo> Tables,
    IReadOnlyList<DatabaseIndexInfo> Indexes,
    IReadOnlyList<DatabaseConnectionInfo> Connections,
    IReadOnlyList<DatabaseSettingInfo> Settings,
    IReadOnlyList<DatabaseExtensionInfo> Extensions);

public sealed record DatabaseServerSummary(
    string ServerVersion,
    string FullVersion,
    string DatabaseName,
    long DatabaseBytes,
    string DatabaseSize,
    Instant ServerStartedAt,
    Instant? StatsResetAt,
    int Connections,
    int MaxConnections,
    double? CacheHitPercent,
    long Commits,
    long Rollbacks,
    long Deadlocks,
    long TempFiles,
    long TempBytes);

/// <summary><see cref="Rows"/> is an exact <c>count(*)</c>, not the planner estimate.</summary>
public sealed record DatabaseTableInfo(
    string Schema,
    string Name,
    long Rows,
    long TotalBytes,
    long TableBytes,
    long IndexBytes,
    long DeadRows,
    long SeqScans,
    long IndexScans,
    long Inserts,
    long Updates,
    long Deletes,
    Instant? LastVacuum,
    Instant? LastAnalyze);

public sealed record DatabaseIndexInfo(
    string Schema,
    string Table,
    string Name,
    long Bytes,
    long Scans,
    bool IsUnique,
    bool IsPrimary);

public sealed record DatabaseConnectionInfo(
    int Pid,
    string? Application,
    string? User,
    string? ClientAddress,
    string? State,
    string? WaitEvent,
    Instant? BackendStart,
    Instant? QueryStart,
    string? Query);

public sealed record DatabaseSettingInfo(string Name, string Value, string Source, string Category);

public sealed record DatabaseExtensionInfo(string Name, string Version, string Schema);

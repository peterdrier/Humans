namespace Humans.Application.Interfaces.Admin;

public interface IAdminDatabaseDiagnosticsService : IApplicationService
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);

    Task<AudienceSegmentation> GetAudienceSegmentationAsync(int? year, CancellationToken ct = default);
}

public sealed record DatabaseMigrationStatus(
    string? LastApplied,
    int AppliedCount,
    int PendingCount,
    IReadOnlyList<string> Applied);

public sealed record AudienceSegmentation(
    int TotalAccounts,
    int WithTicket,
    int WithProfile,
    int WithBoth,
    int WithNeither,
    IReadOnlyList<int> AvailableYears,
    int? SelectedYear);

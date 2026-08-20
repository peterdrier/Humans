using Humans.Base.Interfaces.Admin;
using Humans.Web.Repositories.Admin;

namespace Humans.Web.Services;

internal sealed class AdminDatabaseDiagnosticsService(
    IAdminDatabaseDiagnosticsRepository repository) : IAdminDatabaseDiagnosticsService
{
    public Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default) =>
        repository.GetMigrationStatusAsync(ct);

    public Task<int> ClearHangfireLocksAsync(CancellationToken ct = default) =>
        repository.ClearHangfireLocksAsync(ct);
}

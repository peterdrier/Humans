using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Repositories;

namespace Humans.Web.Repositories.Admin;

/// <summary>
/// Migration and Hangfire-lock state for the admin diagnostics page. Internal, and beside
/// its implementation: a repository is never another section's to inject.
/// </summary>
internal interface IAdminDatabaseDiagnosticsRepository : IRepository
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);
}

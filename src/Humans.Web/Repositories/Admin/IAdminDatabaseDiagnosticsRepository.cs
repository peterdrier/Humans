using Humans.Base.Interfaces.Admin;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Web.Repositories.Admin;

/// <summary>
/// Migration, Hangfire-lock and server/catalog state for the admin diagnostics page. Internal, and beside
/// its implementation: a repository is never another section's to inject.
/// </summary>
internal interface IAdminDatabaseDiagnosticsRepository : IRepository
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);

    Task<DatabaseInfo> GetDatabaseInfoAsync(CancellationToken ct = default);
}

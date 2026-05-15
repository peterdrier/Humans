using Humans.Application.Interfaces.Admin;

namespace Humans.Application.Interfaces.Repositories;

public interface IAdminDatabaseDiagnosticsRepository : IRepository
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);
}

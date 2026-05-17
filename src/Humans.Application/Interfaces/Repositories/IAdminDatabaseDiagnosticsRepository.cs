using Humans.Application.Interfaces.Admin;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

[Section("Admin")]

public interface IAdminDatabaseDiagnosticsRepository : IRepository
{
    Task<DatabaseMigrationStatus> GetMigrationStatusAsync(CancellationToken ct = default);

    Task<int> ClearHangfireLocksAsync(CancellationToken ct = default);
}

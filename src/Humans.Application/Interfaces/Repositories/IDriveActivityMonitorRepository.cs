using Humans.Domain.Attributes;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Compatibility repository for the persistent state owned by the Drive Activity monitor.
/// New code should use the SystemSettings application boundary directly.
/// </summary>
[Section("GoogleIntegration")]
public interface IDriveActivityMonitorRepository : IRepository
{
    Task<Instant?> GetLastRunTimestampAsync(CancellationToken ct = default);

    Task AdvanceLastRunMarkerAsync(
        Instant? newLastRunAt,
        CancellationToken ct = default);
}

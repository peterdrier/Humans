using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

internal interface IBurnSettingsService : IApplicationService
{
    /// <summary>Settings' active event; null when none is configured.</summary>
    Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Any cycle, past or future.</summary>
    Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default);
}

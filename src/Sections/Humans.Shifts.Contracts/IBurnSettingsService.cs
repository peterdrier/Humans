using Humans.Base.Interfaces;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Every consumer is now inside Shifts, so this is a candidate for retirement; kept as the section's external-read
/// seam.
/// </summary>
public interface IBurnSettingsService : IApplicationService
{
    /// <summary>Settings' active event; null when none is configured.</summary>
    Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default);

    /// <summary>Any cycle, past or future.</summary>
    Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default);
}

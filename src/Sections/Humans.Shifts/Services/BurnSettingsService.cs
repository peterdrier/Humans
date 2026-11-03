using Humans.Shifts.Contracts;

namespace Humans.Shifts.Services;

/// <summary>The same composed calendar used by the section's internal readers.</summary>
internal sealed class BurnSettingsService(EventCalendarResolver calendarResolver) : IBurnSettingsService
{
    public Task<BurnSettingsInfo?> GetActiveAsync(CancellationToken ct = default) =>
        calendarResolver.GetActiveAsync(ct);

    public Task<BurnSettingsInfo?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        calendarResolver.GetAsync(id, ct);
}

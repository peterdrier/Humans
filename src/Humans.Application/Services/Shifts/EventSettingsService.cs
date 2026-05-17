using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Application-layer implementation of <see cref="IEventSettingsService"/>.
/// Thin read-only adapter over the Shifts-owned
/// <see cref="IShiftManagementRepository"/> — no caching, no business logic
/// (callers are not on a hot path; <c>event_settings</c> has one active row).
/// </summary>
/// <remarks>
/// Owned by Shifts (matches <c>event_settings</c> table ownership). Cross-section
/// consumers (Events, Camps, Tickets, ...) inject this interface instead of
/// reading <c>DbContext.EventSettings</c> directly (issue nobodies-collective/Humans#719).
/// </remarks>
public sealed class EventSettingsService : IEventSettingsService
{
    private readonly IShiftManagementRepository _repo;

    public EventSettingsService(IShiftManagementRepository repo)
    {
        _repo = repo;
    }

    public Task<EventSettings?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _repo.GetEventSettingsByIdAsync(id, ct);

    public Task<EventSettings?> GetActiveAsync(CancellationToken ct = default) =>
        _repo.GetActiveEventSettingsAsync(ct);

    public async Task<IReadOnlyList<EventSettings>> GetActiveOptionsAsync(CancellationToken ct = default)
    {
        // Invariant: at most one EventSettings.IsActive == true. The method
        // shape returns a list so future multi-event support is non-breaking.
        var active = await _repo.GetActiveEventSettingsAsync(ct);
        return active is null ? [] : [active];
    }
}

using Humans.Settings.Contracts;

namespace Humans.Settings.Services;

/// <summary>
/// The section's own full surface: everything on <see cref="ISettingsService"/>
/// plus the event-settings write. Section-internal by design — nothing outside
/// Settings writes the event values, so the write is not on the cross-section
/// contract and cannot be reached from another section.
/// </summary>
/// <remarks>
/// Both consumers live here: <c>SettingsAdminController</c> and
/// <c>EventSettingsCarryService</c>. Why the key/value <c>SetValueAsync</c> stays
/// on the contract instead: see <see cref="ISettingsService"/>.
/// </remarks>
internal interface ISettingsWriteService : ISettingsService
{
    /// <summary>
    /// Inserts or updates the row identified by <see cref="EventSettingsInfo.Id"/>.
    /// Idempotent: saving the same values twice leaves the row unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another row is already <c>Active</c>, or the row is new and its id names no
    /// Shifts event — new event ids come from the carry, not from here.
    /// </exception>
    Task SaveEventSettingsAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);
}

using Humans.Settings.Contracts;

namespace Humans.Settings.Services;

/// <summary>
/// The section's own full surface: everything on <see cref="ISettingsService"/>
/// plus the event-settings write. Section-internal by design — nothing outside
/// Settings writes the event values, so the write is not on the cross-section
/// contract and cannot be reached from another section.
/// </summary>
/// <remarks>
/// The only consumer: <c>SettingsAdminController</c>. Why the key/value
/// <c>SetValueAsync</c> stays on the contract instead: see
/// <see cref="ISettingsService"/>.
/// </remarks>
internal interface ISettingsWriteService : ISettingsService
{
    /// <summary>
    /// Inserts or updates the row identified by <see cref="EventSettingsInfo.Id"/>, and
    /// writes an <c>AuditAction.EventSettingsUpdated</c> entry naming
    /// <paramref name="actorUserId"/> (peterdrier/Humans#1628 — the Board must be able to
    /// see who changed the event dates). Idempotent: saving the same values twice leaves
    /// the row unchanged, but still audits each call. A blank id mints a brand-new cycle
    /// (nobodies-collective/Humans#1631).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another row is already <c>Active</c>.
    /// </exception>
    Task SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default);
}

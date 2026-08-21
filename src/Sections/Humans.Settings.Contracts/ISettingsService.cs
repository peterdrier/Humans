using Humans.Base.Interfaces;

namespace Humans.Settings.Contracts;

/// <summary>
/// Settings' full application boundary: the reads on
/// <see cref="ISettingsServiceRead"/> plus the writes. A section outside
/// Settings that takes this interface rather than the read one is declaring a
/// cross-section write and must say so with <c>[CrossSectionWrite]</c>
/// (memory/architecture/section-read-write-split.md) — today that is Email's
/// send-pause flag, Monitor's last-run stamp, and the Shifts-owned screen that
/// carries the app-wide values across.
/// </summary>
public interface ISettingsService : ISettingsServiceRead
{
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the event settings row identified by
    /// <see cref="EventSettingsInfo.Id"/>. Idempotent: saving the same values
    /// twice leaves the row unchanged.
    /// </summary>
    Task SaveEventSettingsAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default);
}

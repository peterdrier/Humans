using Humans.Settings.Contracts;

namespace Humans.Settings.Services;

/// <summary>Adds the event-settings write, which is off the cross-section contract.</summary>
internal interface ISettingsWriteService : ISettingsService
{
    /// <summary>
    /// Upserts by <see cref="EventSettingsInfo.Id"/> (blank mints a new cycle) and audits every call, even an unchanged
    /// save, naming <paramref name="actorUserId"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another row is already <c>Active</c>.
    /// </exception>
    Task SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default);
}

using Humans.Settings.Contracts;

namespace Humans.Settings.Services;

internal sealed record EventSettingsSaveResult(bool Succeeded, string? ErrorMessage = null);

/// <summary>Adds the event-settings write, which is off the cross-section contract.</summary>
internal interface ISettingsWriteService : ISettingsService
{
    /// <summary>
    /// Upserts by <see cref="EventSettingsInfo.Id"/> (blank mints a new cycle) and audits every call, even an unchanged
    /// save, naming <paramref name="actorUserId"/>.
    /// </summary>
    /// <returns>An expected refusal when dates are invalid or another row is already active.</returns>
    Task<EventSettingsSaveResult> SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default);
}

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

    /// <summary>Every row, Deleted included, ordered by gate opening date.</summary>
    Task<IReadOnlyList<EventSettingsInfo>> GetAllEventSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="eventSettingsId"/> the active event and the previously active one Inactive, in one
    /// audited step naming <paramref name="actorUserId"/>.
    /// </summary>
    /// <returns>An expected refusal when the row is unknown or Deleted.</returns>
    Task<EventSettingsSaveResult> ActivateEventSettingsAsync(
        Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default);
}

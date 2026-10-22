using NodaTime;

namespace Humans.Events.Contracts;

/// <summary>
/// Carries <see cref="TimeZoneId"/> from Settings' event row so views can render <c>Instant</c>s in local time. An
/// event-settings save marks it stale.
/// </summary>
public sealed record EventGuideSettingsView(
    Guid Id,
    Guid EventSettingsId,
    Instant SubmissionOpenAt,
    Instant SubmissionCloseAt,
    Instant GuidePublishAt,
    int MaxPrintSlots,
    string? TimeZoneId,
    Instant CreatedAt,
    Instant UpdatedAt)
{
    /// <summary>Mirrors <c>EventGuideSettings.IsSubmissionOpenAt</c>.</summary>
    public bool IsSubmissionOpenAt(Instant now) =>
        now >= SubmissionOpenAt && now <= SubmissionCloseAt;
}

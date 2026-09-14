using NodaTime;

namespace Humans.Events.Domain;

/// <summary>
/// Per-account event guide preferences — stores excluded category slugs.
/// One row per user, upserted on change.
/// </summary>
internal sealed class EventPreference
{
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the user. One preference record per user.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// JSON array of category slugs the user has opted out of (e.g. ["adult","spiritual"]).
    /// </summary>
    public string ExcludedCategorySlugs { get; set; } = "[]";
    public Instant UpdatedAt { get; set; }

}

using NodaTime;

namespace Humans.Events.Domain;

/// <summary>One row per user, upserted on change.</summary>
internal sealed class EventPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    /// <summary>JSON array of opted-out category slugs, e.g. ["adult","spiritual"].</summary>
    public string ExcludedCategorySlugs { get; set; } = "[]";
    public Instant UpdatedAt { get; set; }

}

using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-account event guide preferences — stores excluded category slugs.
/// One row per user, upserted on change.
/// </summary>
public class UserGuidePreference
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the user. One preference record per user.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// JSON array of category slugs the user has opted out of (e.g. ["adult","spiritual"]).
    /// </summary>
    public string ExcludedCategorySlugs { get; set; } = "[]";

    /// <summary>
    /// When this preference was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the user.
    /// </summary>
    public User User { get; set; } = null!;
}

using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A language spoken by a member, with proficiency level.
/// Uses ISO 639-1 two-letter language codes.
/// </summary>
public class ProfileLanguage
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the profile.
    /// </summary>
    public Guid ProfileId { get; init; }

    /// <summary>
    /// Navigation property to the profile.
    /// </summary>
    public Profile Profile { get; set; } = null!;

    /// <summary>
    /// ISO 639-1 two-letter language code (e.g., "en", "es", "de").
    /// </summary>
    public string LanguageCode { get; set; } = string.Empty;

    /// <summary>
    /// Self-assessed proficiency level.
    /// </summary>
    public LanguageProficiency Proficiency { get; set; }
}

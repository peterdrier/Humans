
using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// A language spoken by a member, with proficiency level.
/// Uses ISO 639-1 two-letter language codes.
/// </summary>
internal sealed class ProfileLanguage
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public Profile Profile { get; set; } = null!;

    /// <summary>
    /// ISO 639-1 two-letter language code (e.g., "en", "es", "de").
    /// </summary>
    public string LanguageCode { get; set; } = string.Empty;

    public LanguageProficiency Proficiency { get; set; }
}

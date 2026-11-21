
using Humans.Users.Contracts;

namespace Humans.Users.Domain;

internal sealed class ProfileLanguage
{
    public Guid Id { get; init; }

    /// <summary>The owning user.</summary>
    public Guid UserId { get; set; }

    /// <summary>ISO 639-1 two-letter code.</summary>
    public string LanguageCode { get; set; } = string.Empty;

    public LanguageProficiency Proficiency { get; set; }
}

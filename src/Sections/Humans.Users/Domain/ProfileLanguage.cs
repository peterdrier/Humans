
using Humans.Users.Contracts;

namespace Humans.Users.Domain;

internal sealed class ProfileLanguage
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public Profile Profile { get; set; } = null!;

    /// <summary>ISO 639-1 two-letter code.</summary>
    public string LanguageCode { get; set; } = string.Empty;

    public LanguageProficiency Proficiency { get; set; }
}

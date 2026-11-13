
using Humans.Users.Contracts;

namespace Humans.Users.Domain;

internal sealed class ProfileLanguage
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    /// <summary>
    /// The owning user: the key this table is moving to. Null only on rows written before the column existed, until
    /// the <c>users.profile-children-user-id</c> data migration fills them.
    /// </summary>
    public Guid? UserId { get; set; }

    public Profile Profile { get; set; } = null!;

    /// <summary>ISO 639-1 two-letter code.</summary>
    public string LanguageCode { get; set; } = string.Empty;

    public LanguageProficiency Proficiency { get; set; }
}

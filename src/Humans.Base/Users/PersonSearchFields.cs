namespace Humans.Users.Contracts;

/// <summary>
/// The flags are the authorization model: the caller picks buckets, the service confines matching. Emergency
/// contacts are never searchable.
/// </summary>
[Flags]
public enum PersonSearchFields
{
    None = 0,

    /// <summary><c>User.BurnerName</c>. Public.</summary>
    Name = 1 << 0,

    /// <summary>Bio, city, contribution-interests, CV, pronouns, AllActiveProfiles-visible ContactFields, and publicly-exposed emails. Public.</summary>
    Bio = 1 << 1,

    /// <summary>Verified emails (any visibility) and non-public contact fields. The controller must gate with Admin/Board.</summary>
    Admin = 1 << 2,

    /// <summary>Legal names. The controller must gate with admin/coordinator auth: it deanonymizes burners.</summary>
    LegalName = 1 << 3,

    /// <summary>Exact accent- and case-folded full-name equality. Public. Counts burner-name collisions.</summary>
    ExactName = 1 << 4,

    /// <summary>Public endpoints.</summary>
    PublicAll = Name | Bio,

    /// <summary>Admin and Board endpoints only.</summary>
    AdminAll = Name | Bio | LegalName | Admin,
}

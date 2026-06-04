namespace Humans.Application.Services.Profiles;

/// <summary>
/// Bit-flags scoping a person-search to specific Profile/User field buckets.
/// Flags ARE the authorization model: caller picks buckets, service confines matching.
/// Emergency-contact fields never searchable. Implicit scope: not-rejected + not-deleted.
/// </summary>
[Flags]
public enum PersonSearchFields
{
    /// <summary>No fields. Returns no results.</summary>
    None = 0,

    /// <summary>Resolved display name (Profile.BurnerName, falling back to legacy User.DisplayName). Public.</summary>
    Name = 1 << 0,

    /// <summary>Bio, city, contribution-interests, CV, pronouns, AllActiveProfiles-visible ContactFields, and publicly-exposed emails. Public.</summary>
    Bio = 1 << 1,

    /// <summary>Verified email addresses (any visibility) + non-public ContactFields. Controller MUST gate with Admin/Board auth.</summary>
    Admin = 1 << 2,

    /// <summary>Legal FirstName/LastName. Controller MUST gate with admin/coordinator auth — never on public endpoints (deanonymizes burners).</summary>
    LegalName = 1 << 3,

    /// <summary>Resolved display name, matched by exact accent-/case-folded full-string equality (not substring/token). Public. Used to count exact burner-name collisions.</summary>
    ExactName = 1 << 4,

    /// <summary>Name + Bio — public endpoints.</summary>
    PublicAll = Name | Bio,

    /// <summary>Name + Bio + LegalName — admin/coordinator picker endpoints (find people by real name; no private contact data).</summary>
    ManageAll = Name | Bio | LegalName,

    /// <summary>Name + Bio + LegalName + Admin — admin/board endpoints only.</summary>
    AdminAll = Name | Bio | LegalName | Admin,
}

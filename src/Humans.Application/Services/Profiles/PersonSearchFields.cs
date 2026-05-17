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

    /// <summary>BurnerName only (never legacy User.DisplayName). Narrow picker subset.</summary>
    Name = 1 << 0,

    /// <summary>Bio, city, contribution-interests, CV, pronouns, and AllActiveProfiles-visible ContactFields.</summary>
    Bio = 1 << 1,

    /// <summary>Verified email addresses + non-public ContactFields. Controller MUST gate with Admin auth.</summary>
    Admin = 1 << 2,

    /// <summary>Name + Bio — public endpoints.</summary>
    PublicAll = Name | Bio,

    /// <summary>Name + Bio + Admin — admin endpoints only.</summary>
    AdminAll = Name | Bio | Admin,
}

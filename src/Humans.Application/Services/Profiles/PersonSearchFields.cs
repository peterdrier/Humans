namespace Humans.Application.Services.Profiles;

/// <summary>
/// Bit-flags controlling which Profile/User fields a person-search call may
/// match against. The flag is the entire authorization model for the search
/// surface: callers list the buckets they want and the service confines
/// matching to those buckets only. Never include a bit just-in-case — the
/// flags are auditable at a glance, and a non-admin endpoint passing
/// <see cref="Admin"/> is a programmer error caught in code review, not a
/// runtime check (services are auth-free; controllers/handlers gate access).
///
/// <para>
/// Emergency-contact data is explicitly never reachable from any flag
/// combination. The
/// <see cref="Humans.Domain.Entities.Profile.EmergencyContactName"/> /
/// <c>EmergencyContactPhone</c> fields are skipped by the search
/// implementation regardless of input.
/// </para>
///
/// <para>
/// Implicit scope: the service always filters to "not rejected, not
/// deleted". No scope enum is needed — that's the only population anyone
/// is searching.
/// </para>
/// </summary>
[Flags]
public enum PersonSearchFields
{
    /// <summary>No fields. Returns no results.</summary>
    None = 0,

    /// <summary><see cref="Humans.Domain.Entities.Profile.BurnerName"/> +
    /// the underlying <c>User.DisplayName</c> (legal/registered name). The
    /// "narrow picker" subset.</summary>
    Name = 1 << 0,

    /// <summary>Bio, city, contribution-interests, CV entries, pronouns, and
    /// every <see cref="Humans.Domain.Entities.ContactField"/> whose
    /// <see cref="Humans.Domain.Enums.ContactFieldVisibility"/> is
    /// <c>AllActiveProfiles</c> (i.e. publicly visible). The "page-style
    /// search" superset on top of <see cref="Name"/>.</summary>
    Bio = 1 << 1,

    /// <summary>All verified email addresses (<c>UserEmail.Email</c>) plus
    /// every non-public ContactField (BoardOnly / CoordinatorsAndBoard /
    /// MyTeams). Admin auth is required at the controller before passing
    /// this flag. Emergency contact remains excluded.</summary>
    Admin = 1 << 2,

    /// <summary>Convenience: <see cref="Name"/> + <see cref="Bio"/>. Use
    /// this from public/non-admin endpoints; never include
    /// <see cref="Admin"/>.</summary>
    PublicAll = Name | Bio,

    /// <summary>Convenience: <see cref="Name"/> + <see cref="Bio"/> +
    /// <see cref="Admin"/>. Admin endpoints only.</summary>
    AdminAll = Name | Bio | Admin,
}

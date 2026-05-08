namespace Humans.Application.DTOs;

/// <summary>
/// Single canonical person-search result. Returned by
/// <c>IProfileService.SearchProfilesAsync</c> regardless of which
/// <c>PersonSearchFields</c> bits the caller passed; a wider flag-set just
/// lets more rows match.
/// </summary>
/// <param name="UserId">Owning user id.</param>
/// <param name="BurnerName">The human's primary public display label.
/// Falls back to <c>User.DisplayName</c> when
/// <see cref="Humans.Domain.Entities.Profile.BurnerName"/> is unset.</param>
/// <param name="ProfilePictureUrl">Effective picture URL (custom or
/// upstream). <c>null</c> when no picture is set.</param>
/// <param name="MatchField">Short label naming which bucket matched
/// (<c>"Name"</c>, <c>"Bio"</c>, <c>"City"</c>, <c>"Burner CV"</c>,
/// <c>"Email"</c>, etc.). <c>null</c> if the caller doesn't care.</param>
/// <param name="MatchSnippet">Highlighted snippet when the match was on
/// long-form text (Bio / Interests / CV); <c>null</c> for short fields and
/// for name-only matches.</param>
/// <param name="MatchedEmail">The verified email or non-public ContactField
/// value that matched, when the caller passed the
/// <c>PersonSearchFields.Admin</c> bit. Always <c>null</c> for
/// non-admin callers — services are auth-free, but never leak admin-bit
/// content into the basic shape.</param>
public record HumanSearchResult(
    Guid UserId,
    string BurnerName,
    string? ProfilePictureUrl,
    string? MatchField,
    string? MatchSnippet,
    string? MatchedEmail);

using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// One person-search result row, keyed by user id
/// (nobodies-collective/Humans#1062, nobodies-collective/Humans#1061). Callers
/// hold ids and the match context their own search produced; Users owns what a
/// human looks like. Replaces the <c>_HumanSearchResults</c> partial, whose
/// model every caller had to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception).
/// </remarks>
public sealed class UserSearchResultViewComponent : ViewComponent
{
    /// <param name="userId">The matched human.</param>
    /// <param name="matchField">Which bucket matched ("Name", "Bio", …).</param>
    /// <param name="matchSnippet">Highlighted long-form excerpt, when there is one.</param>
    /// <param name="matchedEmail">The address that matched, on admin-bit searches only.</param>
    public IViewComponentResult Invoke(
        Guid userId,
        string? matchField = null,
        string? matchSnippet = null,
        string? matchedEmail = null) =>
        View(new UserSearchResultViewModel(userId, matchField, matchSnippet, matchedEmail));
}

internal sealed record UserSearchResultViewModel(
    Guid UserId,
    string? MatchField,
    string? MatchSnippet,
    string? MatchedEmail);

using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// One person-search result row: callers hold ids and match context, Users owns how a human looks.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal view component
/// ships <c>&lt;vc:…&gt;</c> as inert markup on a green build.
/// </remarks>
public sealed class UserSearchResultViewComponent : ViewComponent
{
    /// <summary>Renders the row from the ids and match context the caller already holds.</summary>
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

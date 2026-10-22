namespace Humans.Users.Contracts;

/// <summary>The one person-search result shape; wider <c>PersonSearchFields</c> just let more rows match.</summary>
/// <param name="UserId">Owning user.</param>
/// <param name="ProfileId">For <c>IContactFieldService</c>, which keys by profile id.</param>
/// <param name="BurnerName">From <c>User.BurnerName</c>.</param>
/// <param name="ProfilePictureUrl">Custom or upstream; null when none.</param>
/// <param name="MatchField">Which bucket matched (<c>"Name"</c>, <c>"Bio"</c>, <c>"Email"</c>, …).</param>
/// <param name="MatchSnippet">For long-form matches (Bio, Interests, CV); null otherwise.</param>
/// <param name="MatchedEmail">Only with the <c>PersonSearchFields.Admin</c> bit; always null otherwise.</param>
/// <param name="Score">Higher is better. Results come back unordered: callers sort with <c>OrderByRelevance()</c>.</param>
public record HumanSearchResult(
    Guid UserId,
    Guid ProfileId,
    string BurnerName,
    string? ProfilePictureUrl,
    string? MatchField,
    string? MatchSnippet,
    string? MatchedEmail,
    int Score);

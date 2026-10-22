using Humans.Base.Helpers;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Picks one owner per address out of <see cref="IUserEmailService.MatchByEmailsAsync"/>,
/// which returns every matching <c>user_emails</c> row in no particular order.
/// </summary>
internal static class UserEmailMatchOwner
{
    /// <summary>
    /// Verified &gt; most recently updated &gt; stable UserId: otherwise an unverified duplicate can outrank the real
    /// owner, and the sync's records land on the wrong member. Keyed by <see cref="GmailAliasEmailComparer"/>.
    /// </summary>
    public static Dictionary<string, UserEmailMatch> ByEmail(IEnumerable<UserEmailMatch> matches) =>
        matches
            .GroupBy(m => m.Email, GmailAliasEmailComparer.Instance)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.IsVerified)
                    .ThenByDescending(m => m.UpdatedAt)
                    .ThenBy(m => m.UserId)
                    .First(),
                GmailAliasEmailComparer.Instance);
}

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
    /// Winner per address: verified &gt; most-recently-updated &gt; stable UserId. Without a
    /// deterministic pick an unverified duplicate can outrank the real owner, and whatever the
    /// sync attributes to that address lands on the wrong human — including in their GDPR export.
    /// </summary>
    public static Dictionary<string, UserEmailMatch> ByEmail(IEnumerable<UserEmailMatch> matches) =>
        matches
            .GroupBy(m => m.Email, NormalizingEmailComparer.Instance)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.IsVerified)
                    .ThenByDescending(m => m.UpdatedAt)
                    .ThenBy(m => m.UserId)
                    .First(),
                NormalizingEmailComparer.Instance);
}

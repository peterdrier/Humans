using Humans.Base.Helpers;
using Humans.Users.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// The one definition of "which user owns this verified email" for matching attendee and
/// buyer emails: verified rows only, gmail/googlemail-normalized, and an email verified by
/// more than one user maps to nobody. The sync builds the mirror from it and the per-user
/// ticket-count fallback reads it, so the two cannot disagree on a collision.
/// </summary>
internal static class VerifiedEmailLookup
{
    public static (Dictionary<string, Guid> Lookup, IReadOnlyList<(string Email, int UserCount)> Collisions)
        Build(IEnumerable<UserInfo> users)
    {
        var entries = users.SelectMany(user => user.UserEmails
            .Where(email => email.IsVerified)
            .Select(email => (email.Email, user.Id)));

        var lookup = new Dictionary<string, Guid>(NormalizingEmailComparer.Instance);
        var collisions = new List<(string Email, int UserCount)>();
        foreach (var group in entries.GroupBy(e => e.Email, NormalizingEmailComparer.Instance))
        {
            var distinctUserIds = group.Select(e => e.Id).Distinct().ToList();
            if (distinctUserIds.Count == 1)
                lookup[group.Key] = distinctUserIds[0];
            else
                collisions.Add((group.Key, distinctUserIds.Count));
        }

        return (lookup, collisions);
    }
}

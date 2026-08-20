using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

// Stateless helper for the admin dashboard's "Preferred language" card. Filters the UserInfo
// snapshot to Active ∪ MissingConsents (pending-deletion split off earlier) before tallying —
// moved out of the deleted admin-dashboard aggregator at nobodies-collective/Humans#1091.
internal static class PreferredLanguageCalculator
{
    public static IReadOnlyList<PreferredLanguageCount> Build(
        IReadOnlyCollection<UserInfo> snapshot, MembershipPartition partition)
    {
        var approvedNotSuspended = new HashSet<Guid>(partition.Active.Concat(partition.MissingConsents));
        return snapshot
            .Where(u => approvedNotSuspended.Contains(u.Id))
            .GroupBy(u => u.PreferredLanguage, StringComparer.Ordinal)
            .Select(g => new PreferredLanguageCount(g.Key, g.Count()))
            .OrderByDescending(l => l.Count)
            .ToList();
    }
}

internal sealed record PreferredLanguageCount(string Language, int Count);

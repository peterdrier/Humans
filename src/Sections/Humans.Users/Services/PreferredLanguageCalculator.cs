using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

// Tallies Active ∪ MissingConsents only; pending deletion is split off earlier.
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

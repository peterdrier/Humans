using Humans.EarlyEntry.Contracts;

namespace Humans.EarlyEntry.Services;

/// <summary>
/// Fans out over the registered providers and collapses per person: earliest date wins,
/// distinct reasons kept. Sequential is a simplicity choice, not a thread-safety
/// requirement (design-rules §8b) — nothing here is slow enough to want otherwise.
/// </summary>
internal sealed class EarlyEntryService(IEnumerable<IEarlyEntryProvider> providers) : IEarlyEntryService
{
    public async Task<IReadOnlyList<EarlyEntryRosterRow>> GetRosterAsync(CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        return all
            .GroupBy(g => g.UserId)
            .Select(grp => Collapse(grp.Key, grp))
            .ToList();
    }

    public async Task<EarlyEntryRosterRow?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        var mine = all.Where(g => g.UserId == userId).ToList();
        return mine.Count == 0 ? null : Collapse(userId, mine);
    }

    private static EarlyEntryRosterRow Collapse(Guid userId, IEnumerable<EarlyEntryGrant> grants)
    {
        var rows = grants.ToList();
        var sources = rows.Select(g => g.Source).Distinct(StringComparer.Ordinal).ToList();
        return new(userId, rows.Min(g => g.EntryDate), sources, sources.Count > 1);
    }

    private async Task<List<EarlyEntryGrant>> GatherAsync(CancellationToken ct)
    {
        var all = new List<EarlyEntryGrant>();
        foreach (var provider in providers)
            all.AddRange(await provider.GetEarlyEntriesAsync(ct));
        return all;
    }
}

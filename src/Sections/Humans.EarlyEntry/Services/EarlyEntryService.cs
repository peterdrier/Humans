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
            .Select(grp =>
            {
                var entry = Collapse(grp);
                return new EarlyEntryRosterRow(grp.Key, entry.EarliestEntryDate, entry.Sources, entry.Sources.Count > 1);
            })
            .ToList();
    }

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        var mine = all.Where(g => g.UserId == userId).ToList();
        return mine.Count == 0 ? null : Collapse(mine);
    }

    private static UserEarlyEntry Collapse(IEnumerable<EarlyEntryGrant> grants) => new(
        grants.Min(g => g.EntryDate),
        grants.Select(g => g.Source).Distinct(StringComparer.Ordinal).ToList());

    private async Task<List<EarlyEntryGrant>> GatherAsync(CancellationToken ct)
    {
        var all = new List<EarlyEntryGrant>();
        foreach (var provider in providers)
            all.AddRange(await provider.GetEarlyEntriesAsync(ct));
        return all;
    }
}

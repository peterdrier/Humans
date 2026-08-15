using Humans.EarlyEntry.Contracts;

namespace Humans.EarlyEntry.Services;

/// <summary>
/// Fans out over every <see cref="IEarlyEntryProvider"/> and assembles per-user
/// EE results. Sequential, not Task.WhenAll: providers share the scoped
/// section DbContexts, which are not thread-safe (same reason GdprExportService is
/// sequential). Owns no repository.
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
                var sources = grp.Select(g => g.Source).Distinct(StringComparer.Ordinal).ToList();
                return new EarlyEntryRosterRow(
                    grp.Key,
                    grp.Min(g => g.EntryDate),
                    sources,
                    sources.Count > 1);
            })
            .ToList();
    }

    public async Task<UserEarlyEntry?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var all = await GatherAsync(ct);
        var mine = all.Where(g => g.UserId == userId).ToList();
        if (mine.Count == 0) return null;
        return new UserEarlyEntry(
            mine.Min(g => g.EntryDate),
            mine.Select(g => g.Source).Distinct(StringComparer.Ordinal).ToList());
    }

    private async Task<List<EarlyEntryGrant>> GatherAsync(CancellationToken ct)
    {
        var all = new List<EarlyEntryGrant>();
        foreach (var provider in providers)
            all.AddRange(await provider.GetEarlyEntriesAsync(ct));
        return all;
    }
}

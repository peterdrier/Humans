using Humans.Base.Interfaces;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin nav as rendered: <see cref="AdminNavTree"/>'s groups merged with what sections
/// contributed through <see cref="ISectionAdminNav"/>.
/// </summary>
/// <remarks>
/// Contributions merge into an existing group by <see cref="AdminNavGroup.GroupKey"/> — several
/// sections share one group ("Tickets", "Money") — and otherwise land as a new group ordered by
/// weight, above the System zone unless the group is itself <see cref="AdminNavGroup.System"/>.
/// Sorting is stable, so items and groups that carry no weight keep the order they were
/// declared in: today's tree encodes traffic-based editorial judgement and must not re-sort.
/// </remarks>
public static class AdminNavComposition
{
    public static IReadOnlyList<AdminNavGroup> Compose(IEnumerable<ISectionAdminNav> contributors)
    {
        var contributed = contributors
            .SelectMany(c => c.Groups())
            .OrderBy(g => g.Weight)
            .ToList();

        if (contributed.Count == 0)
            return AdminNavTree.Groups;

        var merged = new List<AdminNavGroup>(AdminNavTree.Groups);

        foreach (var group in contributed)
        {
            var index = merged.FindIndex(g => string.Equals(g.GroupKey, group.GroupKey, StringComparison.Ordinal));
            if (index < 0)
            {
                merged.Insert(InsertIndex(merged, group), group);
                continue;
            }

            merged[index] = merged[index] with
            {
                Items = [.. merged[index].Items.Concat(group.Items).OrderBy(i => i.Weight)]
            };
        }

        return merged;
    }

    /// <summary>
    /// Where a new group lands: before the first group that outweighs it, and never below the
    /// System zone the sidebar renders as collapsed plumbing — unless the group is itself
    /// System, which appends. The tree's groups all carry weight 0, so a contribution that
    /// asks for none still lands last above that zone, keeping today's order.
    /// </summary>
    private static int InsertIndex(List<AdminNavGroup> merged, AdminNavGroup group)
    {
        if (group.System)
            return merged.Count;

        var heavier = merged.FindIndex(g => !g.System && g.Weight > group.Weight);
        if (heavier >= 0)
            return heavier;

        var systemZone = merged.FindIndex(g => g.System);
        return systemZone < 0 ? merged.Count : systemZone;
    }
}

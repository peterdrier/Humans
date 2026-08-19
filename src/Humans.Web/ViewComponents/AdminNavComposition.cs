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
                // The sidebar renders System groups as collapsed plumbing at the bottom, so a
                // user-facing group goes above that zone rather than below it.
                var systemZone = group.System ? -1 : merged.FindIndex(g => g.System);
                if (systemZone < 0)
                    merged.Add(group);
                else
                    merged.Insert(systemZone, group);
                continue;
            }

            merged[index] = merged[index] with
            {
                Items = [.. merged[index].Items.Concat(group.Items).OrderBy(i => i.Weight)]
            };
        }

        return merged;
    }
}

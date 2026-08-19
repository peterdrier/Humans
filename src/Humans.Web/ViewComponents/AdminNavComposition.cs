using Humans.Base.Interfaces;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The admin nav as rendered: <see cref="AdminNavTree"/>'s groups merged with what sections
/// contributed through <see cref="ISectionAdminNav"/>.
/// </summary>
/// <remarks>
/// Contributions merge into an existing group by <see cref="AdminNavGroup.GroupKey"/> — several
/// sections share one group ("Tickets", "Money") — and otherwise append as a new group ordered
/// by weight. Sorting is stable, so items and groups that carry no weight keep the order they
/// were declared in: today's tree encodes traffic-based editorial judgement and must not re-sort.
/// </remarks>
public static class AdminNavComposition
{
    public static IReadOnlyList<AdminNavGroup> Compose(IEnumerable<ISectionAdminNav> contributors)
    {
        var contributed = contributors
            .SelectMany(c => c.Groups())
            .OrderBy(g => g.Weight)
            .ThenBy(g => g.GroupKey, StringComparer.Ordinal)
            .ToList();

        if (contributed.Count == 0)
            return AdminNavTree.Groups;

        var merged = new List<AdminNavGroup>(AdminNavTree.Groups);

        foreach (var group in contributed)
        {
            var index = merged.FindIndex(g => string.Equals(g.GroupKey, group.GroupKey, StringComparison.Ordinal));
            if (index < 0)
            {
                merged.Add(group);
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

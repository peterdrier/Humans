using Humans.Base.Constants;
using Humans.Issues.Contracts;

namespace Humans.Issues.Domain;

/// <summary>
/// Maps an Issue's <c>Section</c> string to the role(s) whose holders see it
/// in their queue. <see cref="RoleNames.Admin"/> is implicit on every section.
/// Null Section → Admin only.
///
/// <para>
/// The table is not held here: each owning section declares its own queue through
/// <see cref="IIssueQueueOwner"/> and this is the lookup over what DI discovered. A queue key
/// need not equal its section's name — Users declares <c>Profiles</c> and Consent declares
/// <c>Legal</c>, the names their rows were stored under before those renames. A key no section
/// claims routes to nobody and so falls through to the Admin queue, the same fall-through an
/// unknown or tampered value takes; it needs no migration because Section is stored as a free
/// string.
/// </para>
/// </summary>
internal sealed class IssueSectionRouting
{
    private readonly Dictionary<string, IReadOnlyList<string>> _roles;
    private readonly Dictionary<string, string> _canonical;

    public IssueSectionRouting(IEnumerable<IIssueQueueOwner> owners)
    {
        var claimed = owners
            .GroupBy(o => o.QueueKey, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        _roles = claimed.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.SelectMany(o => o.OwningRoles).Distinct(StringComparer.Ordinal)],
            StringComparer.Ordinal);

        _canonical = claimed.ToDictionary(g => g.Key, g => g.Key, StringComparer.OrdinalIgnoreCase);

        AllKnownSections = [.. claimed.Select(g => g.Key)];
    }

    /// <summary>Every queue key a section claims, ordered so the dropdown is stable.</summary>
    public IReadOnlyList<string> AllKnownSections { get; }

    /// <summary>
    /// Roles (besides Admin) that own each section. A user holding any of the
    /// listed roles for a section sees that section's queue. Returns an empty
    /// array for a null or unclaimed section (Admin-only fallback).
    /// </summary>
    public IReadOnlyList<string> RolesFor(string? section) =>
        section is not null && _roles.TryGetValue(section, out var roles) ? roles : [];

    /// <summary>
    /// Whether a viewer holding <paramref name="viewerRoles"/> may handle an issue filed
    /// against <paramref name="section"/> — mutate it, or comment on it as a non-reporter.
    /// Admin handles everything; otherwise the viewer must hold a role that owns the section.
    /// Admin is read out of the role set, never taken as a separate privilege argument.
    /// </summary>
    /// <remarks>
    /// The one statement of the handle rule. Both enforcement points read it: the service,
    /// which gates every per-item read and mutation whichever door they arrive through, and
    /// <c>IssuesAuthorizationHandler</c>, which the browser also asks in order to shape the
    /// page.
    /// </remarks>
    public bool CanHandle(string? section, IReadOnlyCollection<string> viewerRoles)
    {
        var roleSet = viewerRoles.ToHashSet(StringComparer.Ordinal);
        return roleSet.Contains(RoleNames.Admin) || RolesFor(section).Any(roleSet.Contains);
    }

    /// <summary>
    /// Returns the set of section strings whose role list contains any of
    /// <paramref name="userRoles"/>. Used for queue filtering.
    /// </summary>
    public IReadOnlySet<string> SectionsForRoles(IEnumerable<string> userRoles)
    {
        var roleSet = userRoles.ToHashSet(StringComparer.Ordinal);
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in AllKnownSections)
        {
            if (RolesFor(section).Any(roleSet.Contains))
                sections.Add(section);
        }
        return sections;
    }

    /// <summary>
    /// The canonical spelling of a routed section, or null when the value routes to no queue.
    /// Case-insensitive on the way in because the value arrives from a form post; canonical on
    /// the way out because <see cref="RolesFor"/> and the stored column are ordinal
    /// (nobodies-collective/Humans#1509).
    /// </summary>
    public string? Resolve(string? section) =>
        section is not null && _canonical.TryGetValue(section, out var canonical) ? canonical : null;
}

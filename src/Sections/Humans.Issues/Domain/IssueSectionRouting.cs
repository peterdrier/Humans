using Humans.Base.Constants;
using Humans.Issues.Contracts;

namespace Humans.Issues.Domain;

/// <summary>
/// A lookup over the queues DI discovered through <see cref="IIssueQueueOwner"/>. A key need not equal its section's
/// name: Users declares <c>Profiles</c> and Consent <c>Legal</c>, the names their rows were stored under. A key no
/// section claims, like an unknown or tampered value, falls through to the Admin queue.
/// </summary>
internal sealed class IssueSectionRouting
{
    private readonly Dictionary<string, IReadOnlyList<string>> _roles;
    private readonly Dictionary<string, string> _canonical;
    private readonly Dictionary<string, string> _pathSections;

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

        AreaLabelResourceKeys = claimed.ToDictionary(
            g => g.Key, g => g.First().AreaLabelResourceKey, StringComparer.Ordinal);
        _pathSections = claimed.SelectMany(g => g.SelectMany(o => o.PathSegments)
                .Select(segment => (Segment: segment, Queue: g.Key)))
            .GroupBy(p => p.Segment, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Queue, StringComparer.OrdinalIgnoreCase);

        AllKnownSections = [.. claimed.Select(g => g.Key)];
    }

    /// <summary>Keyed by canonical queue name.</summary>
    public IReadOnlyDictionary<string, string> AreaLabelResourceKeys { get; }

    /// <summary>Null when no queue declares the segment.</summary>
    public string? ResolvePathSegment(string segment) => _pathSections.GetValueOrDefault(segment);

    /// <summary>Ordered so the dropdown is stable.</summary>
    public IReadOnlyList<string> AllKnownSections { get; }

    /// <summary>Roles besides Admin. Empty for a null or unclaimed section (Admin-only fallback).</summary>
    public IReadOnlyList<string> RolesFor(string? section) =>
        section is not null && _roles.TryGetValue(section, out var roles) ? roles : [];

    /// <summary>
    /// The one statement of the handle rule (mutate, or comment as a non-reporter), read by both the service and
    /// <c>IssuesAuthorizationHandler</c>. Admin is read out of the role set, never passed separately.
    /// </summary>
    public bool CanHandle(string? section, IReadOnlyCollection<string> viewerRoles)
    {
        var roleSet = viewerRoles.ToHashSet(StringComparer.Ordinal);
        return roleSet.Contains(RoleNames.Admin) || RolesFor(section).Any(roleSet.Contains);
    }

    /// <summary>The queue filter.</summary>
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
    /// Null when the value routes to no queue. Case-insensitive in, because it comes from a form post; canonical out,
    /// because <see cref="RolesFor"/> and the stored column are ordinal.
    /// </summary>
    public string? Resolve(string? section) =>
        section is not null && _canonical.TryGetValue(section, out var canonical) ? canonical : null;
}

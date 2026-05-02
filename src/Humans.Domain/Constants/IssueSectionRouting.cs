namespace Humans.Domain.Constants;

/// <summary>
/// Maps an Issue's <c>Section</c> string to the role(s) whose holders see it
/// in their queue. <see cref="RoleNames.Admin"/> is implicit on every section.
/// Null Section → Admin only.
///
/// <para>
/// This is the routing table — adjust as the org learns. A change here is
/// effective immediately; no migration needed because Section is stored as a
/// free string. Sections referenced here should match the technical names
/// used by the rest of the codebase (e.g. matches <c>docs/sections/*.md</c>).
/// </para>
/// </summary>
public static class IssueSectionRouting
{
    public const string Tickets = "Tickets";
    public const string Camps = "Camps";
    public const string Teams = "Teams";
    public const string Shifts = "Shifts";
    public const string Onboarding = "Onboarding";
    public const string Profiles = "Profiles";
    public const string Budget = "Budget";
    public const string Governance = "Governance";
    public const string Legal = "Legal";
    public const string CityPlanning = "CityPlanning";

    /// <summary>
    /// Roles (besides Admin) that own each section. A user holding any of the
    /// listed roles for a section sees that section's queue. Returns an empty
    /// array for a null section (Admin-only fallback).
    /// </summary>
    public static IReadOnlyList<string> RolesFor(string? section) => section switch
    {
        Tickets => [RoleNames.TicketAdmin],
        Camps => [RoleNames.CampAdmin],
        Teams => [RoleNames.TeamsAdmin],
        Shifts => [RoleNames.NoInfoAdmin],
        Onboarding => [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.HumanAdmin],
        Profiles => [RoleNames.HumanAdmin],
        Budget => [RoleNames.FinanceAdmin],
        Governance => [RoleNames.Board],
        Legal => [RoleNames.ConsentCoordinator],
        CityPlanning => [RoleNames.CampAdmin],
        _ => []
    };

    /// <summary>
    /// Returns the set of section strings whose role list contains any of
    /// <paramref name="userRoles"/>. Used for queue filtering.
    /// </summary>
    public static IReadOnlySet<string> SectionsForRoles(IEnumerable<string> userRoles)
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

    public static readonly IReadOnlyList<string> AllKnownSections =
    [
        Tickets, Camps, Teams, Shifts, Onboarding, Profiles,
        Budget, Governance, Legal, CityPlanning
    ];
}

using System.Diagnostics.CodeAnalysis;
using Humans.Base.Constants;

namespace Humans.Guide.Services;

/// <summary>
/// Maps the display names in guide-heading parentheticals ("Camp Admin") to privilege tokens: a
/// <see cref="RoleNames"/> constant for every entry but <see cref="CampLead"/>.
/// </summary>
internal static class GuideRolePrivilegeMap
{
    /// <summary>
    /// A role on a camp season, never a claim. <c>GuideRoleResolver</c> sets it from <c>ICampLeadDirectory</c>.
    /// </summary>
    public const string CampLead = "CampLead";

    private static readonly IReadOnlyDictionary<string, string> DisplayToRole =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Admin"] = RoleNames.Admin,
            ["Board"] = RoleNames.Board,
            ["Teams Admin"] = RoleNames.TeamsAdmin,
            ["Camp Admin"] = RoleNames.CampAdmin,
            ["Ticket Admin"] = RoleNames.TicketAdmin,
            ["NoInfo Admin"] = RoleNames.NoInfoAdmin,
            ["No Info Admin"] = RoleNames.NoInfoAdmin,
            ["Feedback Admin"] = RoleNames.FeedbackAdmin,
            ["Human Admin"] = RoleNames.HumanAdmin,
            ["Finance Admin"] = RoleNames.FinanceAdmin,
            ["Events Admin"] = RoleNames.EventsAdmin,
            ["Store Admin"] = RoleNames.StoreAdmin,
            ["Consent Coordinator"] = RoleNames.ConsentCoordinator,
            ["Volunteer Coordinator"] = RoleNames.VolunteerCoordinator,
            ["Camp Lead"] = CampLead
        };

    /// <summary>
    /// The roles <see cref="GuideRoleResolver"/> probes against claims; deriving them here keeps the two from drifting.
    /// Excludes <see cref="CampLead"/>: no claim carries it.
    /// </summary>
    public static readonly IReadOnlyList<string> MappedRoles =
        DisplayToRole.Values
            .Where(v => !string.Equals(v, CampLead, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool TryResolve(string displayName, [NotNullWhen(true)] out string? systemRole)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            systemRole = null;
            return false;
        }

        return DisplayToRole.TryGetValue(displayName.Trim(), out systemRole);
    }

    /// <summary>Unknown tokens are skipped, not thrown.</summary>
    public static IReadOnlyList<string> ParseParenthetical(string? paren)
    {
        if (string.IsNullOrWhiteSpace(paren))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var token in paren.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryResolve(token, out var role))
            {
                result.Add(role);
            }
        }
        return result;
    }
}

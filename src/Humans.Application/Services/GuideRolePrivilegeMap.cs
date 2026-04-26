using System.Diagnostics.CodeAnalysis;
using Humans.Domain.Constants;

namespace Humans.Application.Services;

/// <summary>
/// Maps display names that appear in guide-heading parentheticals (e.g. "Camp Admin",
/// "Consent Coordinator") to the system role constants defined in <see cref="RoleNames"/>.
/// </summary>
public static class GuideRolePrivilegeMap
{
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
            ["Consent Coordinator"] = RoleNames.ConsentCoordinator,
            ["Volunteer Coordinator"] = RoleNames.VolunteerCoordinator
        };

    public static bool TryResolve(string displayName, [NotNullWhen(true)] out string? systemRole)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            systemRole = null;
            return false;
        }

        return DisplayToRole.TryGetValue(displayName.Trim(), out systemRole);
    }

    /// <summary>
    /// Parses a guide-heading parenthetical like "Camp Admin, Finance Admin" into the
    /// matching system-role constants. Unknown tokens are skipped (not thrown).
    /// </summary>
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

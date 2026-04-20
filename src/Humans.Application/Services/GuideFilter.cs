using System.Text.RegularExpressions;
using Humans.Application.Models;
using Humans.Domain.Constants;

namespace Humans.Application.Services;

/// <summary>
/// Strips role-scoped blocks the current user is not entitled to see. Operates on the
/// HTML produced by <c>GuideRenderer</c> (with &lt;div data-guide-role&gt; wrappers).
/// Pure function — returns the filtered HTML.
/// </summary>
public static class GuideFilter
{
    /// <summary>
    /// Regex that matches the innermost &lt;/div&gt; (non-greedy .*?), which is safe because
    /// guide markdown is PR-reviewed and does not contain nested &lt;div&gt;s.
    /// </summary>
    private static readonly Regex BlockPattern = new(
        """<div\s+data-guide-role="(?<role>[^"]+)"\s+data-guide-roles="(?<roles>[^"]*)"\s*>(?<body>.*?)</div>""",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));

    public static string Apply(string html, GuideRoleContext context)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(context);

        // Two-pass: first pass decides each block's visibility; second pass applies the
        // within-file Coordinator superset (Coordinator inherits from Board/Admin).
        var fileSeesBoardAdmin = false;
        var matches = BlockPattern.Matches(html).ToList();

        foreach (Match match in matches)
        {
            var role = match.Groups["role"].Value;
            var roles = match.Groups["roles"].Value;
            if (role.Equals("boardadmin", StringComparison.Ordinal) &&
                IsVisible(role, roles, context))
            {
                fileSeesBoardAdmin = true;
                break;
            }
        }

        return BlockPattern.Replace(html, match =>
        {
            var role = match.Groups["role"].Value;
            var roles = match.Groups["roles"].Value;

            var visible = IsVisible(role, roles, context);
            if (!visible && role.Equals("coordinator", StringComparison.Ordinal) && fileSeesBoardAdmin)
            {
                visible = true;
            }

            return visible ? match.Value : string.Empty;
        });
    }

    private static bool IsVisible(string role, string rolesAttr, GuideRoleContext context)
    {
        var parenthetical = string.IsNullOrEmpty(rolesAttr)
            ? []
            : (IReadOnlyList<string>)rolesAttr.Split(',', StringSplitOptions.RemoveEmptyEntries);

        return role switch
        {
            "volunteer" => true,
            "coordinator" => IsCoordinatorVisible(parenthetical, context),
            "boardadmin" => IsBoardAdminVisible(parenthetical, context),
            _ => false
        };
    }

    private static bool IsCoordinatorVisible(IReadOnlyList<string> paren, GuideRoleContext ctx)
    {
        if (ctx.IsTeamCoordinator) return true;
        if (ctx.SystemRoles.Contains(RoleNames.Board) || ctx.SystemRoles.Contains(RoleNames.Admin)) return true;
        foreach (var role in paren)
        {
            if (ctx.SystemRoles.Contains(role)) return true;
        }
        return false;
    }

    private static bool IsBoardAdminVisible(IReadOnlyList<string> paren, GuideRoleContext ctx)
    {
        if (ctx.SystemRoles.Contains(RoleNames.Board) || ctx.SystemRoles.Contains(RoleNames.Admin)) return true;
        foreach (var role in paren)
        {
            if (ctx.SystemRoles.Contains(role)) return true;
        }
        return false;
    }
}

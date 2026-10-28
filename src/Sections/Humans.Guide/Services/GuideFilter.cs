using Humans.Base.Constants;

namespace Humans.Guide.Services;

/// <summary>Runs before rendering, so Markdig only ever sees content the reader may read.</summary>
internal static class GuideFilter
{
    public static string Apply(GuideDocument document, GuideRoleContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        // Two-pass: pass 1 detects boardadmin visibility anywhere in the file; pass 2 promotes
        // Coordinator on the strength of it (the within-file superset rule).
        var fileSeesBoardAdmin = document.Segments.Any(s =>
            string.Equals(s.Role, GuideDocument.BoardAdmin, StringComparison.Ordinal) &&
            IsVisible(s, context));

        return string.Join('\n', document.Segments
            .Where(s => IsSegmentVisible(s, context, fileSeesBoardAdmin))
            .Select(s => s.Markdown));
    }

    private static bool IsSegmentVisible(
        GuideSegment segment,
        GuideRoleContext context,
        bool fileSeesBoardAdmin)
    {
        if (segment.Role is null)
        {
            return true;
        }

        if (IsVisible(segment, context))
        {
            return true;
        }

        return fileSeesBoardAdmin &&
            string.Equals(segment.Role, GuideDocument.Coordinator, StringComparison.Ordinal);
    }

    private static bool IsVisible(GuideSegment segment, GuideRoleContext context) =>
        segment.Role switch
        {
            GuideDocument.Volunteer => true,
            GuideDocument.Coordinator => IsCoordinatorVisible(segment.Privileges, context),
            GuideDocument.BoardAdmin => IsBoardAdminVisible(segment.Privileges, context),
            _ => false
        };

    private static bool IsCoordinatorVisible(IReadOnlyList<string> paren, GuideRoleContext ctx)
    {
        if (ctx.IsTeamCoordinator) return true;
        // Camp lead is not a claim, so it is gated on the parenthetical rather than opening
        // every Coordinator block the way IsTeamCoordinator does.
        if (ctx.IsCampLead && paren.Contains(GuideRolePrivilegeMap.CampLead, StringComparer.Ordinal)) return true;
        return IsBoardAdminVisible(paren, ctx);
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

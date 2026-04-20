namespace Humans.Application.Models;

public sealed record GuideRoleContext(
    bool IsAuthenticated,
    bool IsTeamCoordinator,
    IReadOnlySet<string> SystemRoles)
{
    public static readonly GuideRoleContext Anonymous =
        new(false, false, new HashSet<string>(StringComparer.Ordinal));
}

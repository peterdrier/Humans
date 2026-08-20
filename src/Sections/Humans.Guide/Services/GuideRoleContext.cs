namespace Humans.Guide.Services;

internal sealed record GuideRoleContext(
    bool IsAuthenticated,
    bool IsTeamCoordinator,
    bool IsCampLead,
    IReadOnlySet<string> SystemRoles)
{
    public static readonly GuideRoleContext Anonymous =
        new(false, false, false, new HashSet<string>(StringComparer.Ordinal));
}

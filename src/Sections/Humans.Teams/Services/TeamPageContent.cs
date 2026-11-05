using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamPageContent(
    ITeamRepository repo,
    IClock clock,
    IAuditLogService auditLogService,
    ILogger<TeamService> logger) : IApplicationService
{
    public async Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedCallsToAction = callsToAction
            .Where(c => !string.IsNullOrWhiteSpace(c.Text) && !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new CallToAction { Text = c.Text!.Trim(), Url = c.Url!.Trim(), Style = c.Style })
            .ToList();

        try
        {
            var team = await repo.FindForMutationAsync(teamId, cancellationToken);
            if (team is null)
                return Refuse($"Team {teamId} not found");

            if (normalizedCallsToAction.Count > 3)
                return Refuse("A team can have at most 3 calls to action.");

            if (normalizedCallsToAction.Count(c => c.Style == CallToActionStyle.Primary) > 1)
                return Refuse("Only one primary call to action is allowed.");

            if (normalizedCallsToAction.Any(c => !IsSafeCallToActionUrl(c.Url)))
                return Refuse("Calls to action require a relative URL or an HTTP, HTTPS or mailto URL.");

            var canBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;

            if (isPublicPage && !canBePublic)
                return Refuse("Only departments (non-system, top-level teams) can be made public.");

            var now = clock.GetCurrentInstant();
            team.PageContent = pageContent;
            team.CallsToAction = normalizedCallsToAction;
            team.IsPublicPage = isPublicPage;
            team.ShowCoordinatorsOnPublicPage = canBePublic && isPublicPage && showCoordinatorsOnPublicPage;
            team.PageContentUpdatedAt = now;
            team.PageContentUpdatedByUserId = updatedByUserId;
            team.UpdatedAt = now;

            var conflict = await repo.UpdateTeamAsync(team, cancellationToken);
            if (conflict != TeamUpdateConflict.None)
            {
                logger.LogWarning("Team page update rejected for team {TeamId}: {Conflict}", teamId, conflict);
                return new TeamPageUpdateResult(false, null);
            }

            await auditLogService.LogAsync(
                TeamsAuditActions.TeamPageContentUpdated, nameof(Team), teamId,
                $"Team page content updated. Public: {isPublicPage}",
                updatedByUserId);

            return TeamPageUpdateResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to update team page for team {TeamId} by user {UserId}", teamId, updatedByUserId);
            return new TeamPageUpdateResult(false, null);
        }

        TeamPageUpdateResult Refuse(string reason)
        {
            logger.LogWarning("Team page update rejected for team {TeamId} by user {UserId}: {Reason}", teamId, updatedByUserId, reason);
            return TeamPageUpdateResult.Failed(reason);
        }
    }

    // Shared by writes and the page projection, so unsafe legacy values never become links.
    internal static bool IsSafeCallToActionUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Any(char.IsControl)
        && Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
        && (!uri.IsAbsoluteUri || uri.Scheme is "http" or "https" or "mailto");
}

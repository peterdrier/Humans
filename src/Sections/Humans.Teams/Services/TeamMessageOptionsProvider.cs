using Humans.AuditLog.Contracts;
using Humans.Base;
using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Teams.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Team-message eligibility, available send-as teams, and delivery. The controller authorizes the selected team
/// against these options before requesting delivery.
/// </summary>
internal interface ITeamMessageOptionsProvider : IApplicationService
{
    Task<TeamMessageContext> GetContextAsync(Guid viewerId, Guid recipientId, CancellationToken ct = default);
    /// <summary>Queues and audits a message using the resolved participants and a team authorized by the caller.</summary>
    [ExternalWrite]
    Task<bool> SendAsync(TeamMessageContext context, TeamMessageOption team, string message, bool includeContactInfo);
}

internal enum TeamMessageRefusal { MissingUser, Self, OptedOut }

internal sealed record TeamMessageContext(
    UserInfo? Sender, UserInfo? Recipient, IReadOnlyList<TeamMessageOption> Teams, TeamMessageRefusal? Refusal = null);

internal sealed record TeamMessageOption(Guid TeamId, string TeamName, string GoogleGroupEmail);

internal sealed class TeamMessageOptionsProvider(
    ITeamService teamService,
    ITeamResourceService teamResourceService,
    IUserService users,
    ICommunicationPreferenceService commPrefs,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLogService,
    ILogger<TeamMessageOptionsProvider> logger) : ITeamMessageOptionsProvider
{
    public async Task<TeamMessageContext> GetContextAsync(Guid viewerId, Guid recipientId, CancellationToken ct = default)
    {
        var sender = await users.GetUserInfoAsync(viewerId, ct);
        if (sender is null)
            return new(null, null, [], TeamMessageRefusal.MissingUser);
        if (sender.Id == recipientId || sender.AllUserIds.Contains(recipientId))
            return new(sender, sender, [], TeamMessageRefusal.Self);
        var recipient = await users.GetUserInfoAsync(recipientId, ct);
        if (recipient is null)
            return new(sender, null, [], TeamMessageRefusal.MissingUser);
        if (sender.Id == recipient.Id)
            return new(sender, recipient, [], TeamMessageRefusal.Self);
        if (!await commPrefs.AcceptsFacilitatedMessagesAsync(recipient.Id, ct))
            return new(sender, recipient, [], TeamMessageRefusal.OptedOut);
        return new(sender, recipient, await GetOptionsAsync(sender.Id, ct));
    }

    public async Task<bool> SendAsync(TeamMessageContext context, TeamMessageOption team, string message, bool includeContactInfo)
    {
        if (context.Refusal is not null || context.Sender is not { } sender || context.Recipient is not { } recipient
            || string.IsNullOrWhiteSpace(recipient.Email) || string.IsNullOrWhiteSpace(sender.Email))
        {
            logger.LogWarning("Team message refused for sender {SenderUserId} and recipient {RecipientUserId}: {Reason}",
                context.Sender?.Id, context.Recipient?.Id, context.Refusal?.ToString() ?? "Missing email address");
            return false;
        }

        // Queueing and its audit must finish even if the browser request ends.
        await emailService.SendAsync(emailMessages.FacilitatedMessage(
            recipient.Id, recipient.Email, recipient.BurnerName, sender.BurnerName, message, includeContactInfo, sender.Email,
            recipient.PreferredLanguage.IsSupportedCultureCode() ? recipient.PreferredLanguage : CultureCatalog.DefaultCultureCode,
            replyTo: team.GoogleGroupEmail), CancellationToken.None);

        // Name and link the team; never copy its address into the audit text.
        var contactInfo = includeContactInfo ? "yes" : "no";
        try
        {
            await auditLogService.LogAsync(
                EmailAuditActions.FacilitatedMessageSent, AuditSubjectTypes.User, recipient.Id,
                $"Message sent to {recipient.BurnerName} from team {team.TeamName} (contact info shared: {contactInfo})",
                sender.Id, relatedEntityId: team.TeamId, relatedEntityType: "Team");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Facilitated-message audit failed AFTER queueing a message to user {RecipientUserId} from user {SenderUserId} as team {TeamId} " +
                "(contact info shared: {IncludeContactInfo}). Email is queued; reconcile audit trail.",
                recipient.Id, sender.Id, team.TeamId, includeContactInfo);
        }
        return true;
    }

    public async Task<IReadOnlyList<TeamMessageOption>> GetOptionsAsync(Guid viewerId, CancellationToken ct = default)
    {
        var coordinatedTeams = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive
                && t.GoogleGroupPrefix is not null
                && t.Members.Any(m => m.UserId == viewerId && m.Role == TeamMemberRole.Coordinator))
            .ToList();
        if (coordinatedTeams.Count == 0)
            return [];

        var resourcesByTeam = await teamResourceService.GetResourcesByTeamIdsAsync(
            coordinatedTeams.Select(t => t.Id).ToList(), ct);

        return coordinatedTeams
            .Where(t => resourcesByTeam.GetValueOrDefault(t.Id, [])
                .Any(r => IsSyncedGroup(r, GroupUrl(t.GoogleGroupPrefix!))))
            .Select(t => new TeamMessageOption(t.Id, t.Name, t.GoogleGroupEmail!))
            .ToList();
    }

    // Url is the one field both Google write paths store the same way and key their duplicate checks on.
    // Name differs per path and GoogleId is Google's numeric id, so neither identifies the group on every row.
    private static string GroupUrl(string prefix) =>
        $"https://groups.google.com/a/{DomainConstants.GoogleGroupDomain}/g/{prefix}";

    private static bool IsSyncedGroup(GoogleResourceSnapshot resource, string groupUrl) =>
        resource.ResourceType == GoogleResourceType.Group
        && resource.IsActive
        && resource.LastSyncedAt is not null
        && resource.ErrorMessage is null
        && string.Equals(resource.Url?.TrimEnd('/'), groupUrl, StringComparison.OrdinalIgnoreCase);
}

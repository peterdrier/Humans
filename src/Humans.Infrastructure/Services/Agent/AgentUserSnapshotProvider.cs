using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Models;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentUserSnapshotProvider : IAgentUserSnapshotProvider
{
    private readonly IProfileService _profiles;
    private readonly IUserService _users;
    private readonly IRoleAssignmentService _roles;
    private readonly ITeamService _teams;
    private readonly IConsentService _consents;
    private readonly IFeedbackService _feedback;

    public AgentUserSnapshotProvider(
        IProfileService profiles,
        IUserService users,
        IRoleAssignmentService roles,
        ITeamService teams,
        IConsentService consents,
        IFeedbackService feedback)
    {
        _profiles = profiles;
        _users = users;
        _roles = roles;
        _teams = teams;
        _consents = consents;
        _feedback = feedback;
    }

    public async Task<AgentUserSnapshot> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetProfileAsync(userId, cancellationToken);
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        var activeRoles = await _roles.GetActiveForUserAsync(userId, cancellationToken);
        var teamNames = await _teams.GetActiveTeamNamesForUserAsync(userId, cancellationToken);
        var pendingDocs = await _consents.GetPendingDocumentNamesAsync(userId, cancellationToken);
        var openFeedback = await _feedback.GetOpenFeedbackIdsForUserAsync(userId, cancellationToken);

        var roleAssignments = activeRoles
            .Select(r => (r.RoleName, r.ValidTo?.ToInvariantInstantString() ?? "—"))
            .ToList();

        return new AgentUserSnapshot(
            UserId: userId,
            DisplayName: user?.DisplayName ?? string.Empty,
            PreferredLocale: user?.PreferredLanguage ?? "es",
            Tier: profile?.MembershipTier.ToString() ?? "Volunteer",
            IsApproved: profile?.IsApproved ?? false,
            RoleAssignments: roleAssignments,
            Teams: teamNames,
            PendingConsentDocs: pendingDocs,
            // Tickets not integrated in Phase 1 (no per-user open-ticket lookup method on ITicketQueryService)
            OpenTicketIds: Array.Empty<Guid>(),
            OpenFeedbackIds: openFeedback);
    }
}

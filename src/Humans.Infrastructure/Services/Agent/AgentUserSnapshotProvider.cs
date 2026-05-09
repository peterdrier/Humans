using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Models;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentUserSnapshotProvider : IAgentUserSnapshotProvider
{
    private readonly IProfileService _profiles;
    private readonly IUserService _users;
    private readonly IRoleAssignmentService _roles;
    private readonly ITeamService _teams;
    private readonly IConsentService _consents;
    private readonly IFeedbackService _feedback;
    private readonly ITicketQueryService _tickets;
    private readonly IShiftSignupService _shiftSignups;
    private readonly IShiftManagementService _shiftManagement;
    private readonly IClock _clock;

    public AgentUserSnapshotProvider(
        IProfileService profiles,
        IUserService users,
        IRoleAssignmentService roles,
        ITeamService teams,
        IConsentService consents,
        IFeedbackService feedback,
        ITicketQueryService tickets,
        IShiftSignupService shiftSignups,
        IShiftManagementService shiftManagement,
        IClock clock)
    {
        _profiles = profiles;
        _users = users;
        _roles = roles;
        _teams = teams;
        _consents = consents;
        _feedback = feedback;
        _tickets = tickets;
        _shiftSignups = shiftSignups;
        _shiftManagement = shiftManagement;
        _clock = clock;
    }

    public async Task<AgentUserSnapshot> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetProfileAsync(userId, cancellationToken);
        var user = await _users.GetByIdAsync(userId, cancellationToken);
        var activeRoles = await _roles.GetActiveForUserAsync(userId, cancellationToken);
        var teamMemberships = await _teams.GetActiveTeamMembershipsForUserAsync(userId, cancellationToken);
        var pendingDocs = await _consents.GetPendingDocumentNamesAsync(userId, cancellationToken);
        var openFeedback = await _feedback.GetOpenFeedbackIdsForUserAsync(userId, cancellationToken);
        var openTickets = await _tickets.GetOpenTicketIdsForUserAsync(userId, cancellationToken);
        var upcomingShifts = await LoadUpcomingShiftsAsync(userId, cancellationToken);

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
            Teams: teamMemberships,
            PendingConsentDocs: pendingDocs,
            OpenTicketIds: openTickets,
            OpenFeedbackIds: openFeedback,
            UpcomingShifts: upcomingShifts);
    }

    private async Task<IReadOnlyList<UpcomingShiftEntry>> LoadUpcomingShiftsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var activeEvent = await _shiftManagement.GetActiveAsync();
        if (activeEvent is null)
            return Array.Empty<UpcomingShiftEntry>();

        var signups = await _shiftSignups.GetByUserAsync(userId, activeEvent.Id);
        if (signups.Count == 0)
            return Array.Empty<UpcomingShiftEntry>();

        var now = _clock.GetCurrentInstant();
        var upcoming = signups
            .Where(s => s.Shift is not null
                && s.Shift.GetAbsoluteEnd(activeEvent) > now
                && s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .ToList();
        if (upcoming.Count == 0)
            return Array.Empty<UpcomingShiftEntry>();

        var entries = new List<UpcomingShiftEntry>();

        // Singletons (SignupBlockId == null) → one entry per signup.
        foreach (var signup in upcoming.Where(s => s.SignupBlockId is null))
        {
            var date = activeEvent.GateOpeningDate.PlusDays(signup.Shift.DayOffset);
            entries.Add(new UpcomingShiftEntry(
                Key: signup.Id,
                Label: signup.Shift.Rota?.Name ?? "(unnamed rota)",
                StartDate: date,
                EndDate: date,
                DayCount: 1,
                Status: signup.Status));
        }

        // Block signups (SignupBlockId != null) → group by block id.
        foreach (var group in upcoming
            .Where(s => s.SignupBlockId is not null)
            .GroupBy(s => s.SignupBlockId!.Value))
        {
            var dates = group
                .Select(s => activeEvent.GateOpeningDate.PlusDays(s.Shift.DayOffset))
                .OrderBy(d => d)
                .ToList();
            var label = group.First().Shift.Rota?.Name ?? "(unnamed rota)";
            // Use the earliest signup's status as the block status — block
            // signups are created in one transaction so they share a status,
            // but defensively pick the earliest so a partially-bailed range
            // still surfaces something meaningful.
            var status = group.OrderBy(s => s.Shift.DayOffset).First().Status;
            entries.Add(new UpcomingShiftEntry(
                Key: group.Key,
                Label: label,
                StartDate: dates[0],
                EndDate: dates[^1],
                DayCount: dates.Distinct().Count(),
                Status: status));
        }

        // No display sort here — AgentPromptAssembler.BuildUserContextTail
        // sorts by StartDate at the rendering layer (memory/architecture/display-sort-in-controllers.md).
        return entries;
    }
}

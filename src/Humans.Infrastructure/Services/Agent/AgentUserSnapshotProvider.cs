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
using Humans.Domain.Entities;
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
        var upcomingShifts = await BuildUpcomingShiftsAsync(userId);

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

    private async Task<IReadOnlyList<UpcomingShiftEntry>> BuildUpcomingShiftsAsync(Guid userId)
    {
        var eventSettings = await _shiftManagement.GetActiveAsync();
        if (eventSettings is null)
            return Array.Empty<UpcomingShiftEntry>();

        var signups = await _shiftSignups.GetByUserAsync(userId, eventSettings.Id);
        if (signups.Count == 0)
            return Array.Empty<UpcomingShiftEntry>();

        var now = _clock.GetCurrentInstant();
        var upcoming = signups
            .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .Where(s => s.Shift.GetAbsoluteEnd(eventSettings) > now)
            .ToList();

        if (upcoming.Count == 0)
            return Array.Empty<UpcomingShiftEntry>();

        // Group by SignupBlockId — null block id collapses to one entry per
        // signup (singleton); non-null block id collapses every signup in
        // the block into one entry.
        var entries = new List<UpcomingShiftEntry>();
        foreach (var group in upcoming.GroupBy(s => s.SignupBlockId))
        {
            if (group.Key is null)
            {
                foreach (var s in group)
                {
                    var date = LocalDateForOffset(eventSettings, s.Shift.DayOffset);
                    entries.Add(new UpcomingShiftEntry(
                        Key: s.Id,
                        Label: s.Shift.Rota.Name,
                        StartDate: date,
                        EndDate: date,
                        DayCount: 1,
                        Status: s.Status));
                }
            }
            else
            {
                var ordered = group.OrderBy(s => s.Shift.DayOffset).ToList();
                var first = ordered[0];
                var last = ordered[^1];
                var startDate = LocalDateForOffset(eventSettings, first.Shift.DayOffset);
                var endDate = LocalDateForOffset(eventSettings, last.Shift.DayOffset);
                // Distinct day count — a block normally has one shift per
                // calendar day, but we count distinct DayOffset values to
                // be safe in case a block ever spans multiple shifts on
                // the same day.
                var dayCount = ordered.Select(s => s.Shift.DayOffset).Distinct().Count();
                entries.Add(new UpcomingShiftEntry(
                    Key: group.Key.Value,
                    Label: first.Shift.Rota.Name,
                    StartDate: startDate,
                    EndDate: endDate,
                    DayCount: dayCount,
                    // Block status — Pending wins if any signup is still
                    // awaiting approval, otherwise the shared Confirmed
                    // status.
                    Status: ordered.Any(s => s.Status == SignupStatus.Pending)
                        ? SignupStatus.Pending
                        : SignupStatus.Confirmed));
            }
        }

        return entries
            .OrderBy(e => e.StartDate)
            .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LocalDate LocalDateForOffset(EventSettings eventSettings, int dayOffset) =>
        eventSettings.GateOpeningDate.PlusDays(dayOffset);
}

using Humans.Auth.Contracts;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Auth;
using Humans.Consent.Contracts;
using Humans.Feedback.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Models;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Agent.Models;
using Humans.Users.Contracts;

namespace Humans.Agent.Services;

internal sealed class AgentUserSnapshotProvider(
    IUserServiceRead users,
    IRoleAssignmentService roles,
    ITeamServiceRead teams,
    IConsentServiceRead consents,
    IFeedbackServiceRead feedback,
    ITicketServiceRead tickets,
    IShiftView shiftView,
    IBurnSettingsService burnSettings,
    IClock clock) : IAgentUserSnapshotProvider
{
    public async Task<AgentUserSnapshot> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetUserInfoAsync(userId, cancellationToken);
        var profile = user?.Profile;
        var activeRoles = await roles.GetActiveForUserAsync(userId, cancellationToken);
        var teamMemberships = (await teams.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.IsActive && t.SystemTeamType != SystemTeamType.Volunteers)
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == userId) })
            .Where(x => x.Membership is not null)
            .Select(x => new TeamMembership(x.TeamInfo.Name, x.Membership!.Role) { IsHidden = x.TeamInfo.IsHidden })
            .ToList();
        var pendingDocs = await consents.GetPendingDocumentNamesAsync(userId, cancellationToken);
        var openFeedback = await feedback.GetOpenFeedbackIdsForUserAsync(userId, cancellationToken);
        var ticketHoldings = await tickets.GetUserTicketHoldingsAsync(userId, cancellationToken);
        var openTickets = ticketHoldings.OpenTicketOrderIds;
        var upcomingShifts = await LoadUpcomingShiftsAsync(userId, cancellationToken);

        var roleAssignments = activeRoles
            .Select(r => (r.RoleName, r.ValidTo?.ToIso8601() ?? "—"))
            .ToList();

        return new AgentUserSnapshot(
            UserId: userId,
            DisplayName: user?.BurnerName ?? string.Empty,
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
        var activeEvent = await burnSettings.GetActiveAsync(cancellationToken);
        if (activeEvent is null)
            return [];

        // T-09 (issue #720): read signups from the cached ShiftUserView
        // rather than IShiftSignupService.GetByUserAsync. The view already
        // filters Signups to the active event (see ShiftViewService).
        var userView = await shiftView.GetUserAsync(userId, cancellationToken);
        var signups = userView.Signups;
        if (signups.Count == 0)
            return [];

        var now = clock.GetCurrentInstant();
        var upcoming = signups
            .Where(s => s.AbsoluteEnd > now
                && s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .ToList();
        if (upcoming.Count == 0)
            return [];

        var entries = new List<UpcomingShiftEntry>();

        // Singletons (SignupBlockId == null) → one entry per signup.
        foreach (var signup in upcoming.Where(s => s.SignupBlockId is null))
        {
            var date = signup.Date;
            entries.Add(new UpcomingShiftEntry(
                Key: signup.Id,
                Label: RotaLabel(signup),
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
                .Select(s => s.Date)
                .OrderBy(d => d)
                .ToList();
            var label = RotaLabel(group.First());
            // Use the earliest signup's status as the block status — block
            // signups are created in one transaction so they share a status,
            // but defensively pick the earliest so a partially-bailed range
            // still surfaces something meaningful.
            var status = group.OrderBy(s => s.Date).First().Status;
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

    /// <summary>Rota display name, with the unnamed-rota fallback the entity walk used to carry.</summary>
    private static string RotaLabel(ShiftSignupSummary signup) =>
        string.IsNullOrWhiteSpace(signup.RotaName) ? "(unnamed rota)" : signup.RotaName;
}

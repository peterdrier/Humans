using Humans.Auth.Contracts;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using NodaTime;

namespace Humans.Users.Models;

/// <summary>Gathers each owning section's facts about one account for the merge page's profile card.</summary>
internal sealed class MergeAccountCardBuilder(
    IUserManagement userService,
    ITeamService teamService,
    ICampService campService,
    IGovernanceService governanceService,
    ISystemRoleService roleAssignments,
    IShiftView shiftView,
    ITicketService ticketService,
    IClock clock)
{
    public async Task<MergeAccountCardViewModel?> BuildAsync(Guid userId, bool isSignedIn, CancellationToken ct)
    {
        // Raw: the card describes this one account, not whatever it may resolve to.
        var info = await userService.GetRawUserInfoAsync(userId, ct);
        if (info is null) return null;

        var memberships = (await teamService.GetUserTeamMembershipsAsync(userId, ct))
            .Where(m => m.IsProfileMembership)
            .Select(m => new TeamMembership(m.TeamName, m.Role) { IsHidden = m.IsHidden })
            .ToList();
        var summary = info.Profile is null
            ? new ProfileSummaryViewModel { UserId = userId, PreferredLanguage = info.PreferredLanguage }
            : ProfileSummaryViewModelBuilder.BuildWithProfile(
                info, memberships,
                await governanceService.GetMembershipTierAsync(userId, ct),
                await roleAssignments.IsUserBoardMemberAsync(userId, ct),
                await campService.GetCampUserInfoAsync(userId, ct));
        summary.LegalName = info.FullName;
        summary.CampVisibleToViewer = true;
        // An account nobody filled in reads as "imported, no profile yet".
        summary.HasProfile = info.Profile is not null && info.State != UserState.Bare;

        var now = clock.GetCurrentInstant();
        var signups = (await shiftView.GetUserAsync(userId, ct)).Signups;
        var googleAddresses = info.UserEmails
            .Where(e => e.IsVerified && e.Provider is not null)
            .Select(e => e.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new MergeAccountCardViewModel
        {
            Summary = summary,
            IsSignedIn = isSignedIn,
            Roles = (await roleAssignments.GetActiveForUserAsync(userId, ct))
                .Select(r => r.RoleName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList(),
            ShiftsAhead = signups.Count(s => s.IsActive && s.AbsoluteEnd > now),
            ShiftsWorked = signups.Count(s => s.Status == SignupStatus.Confirmed && s.AbsoluteEnd <= now),
            Tickets = (await ticketService.GetUserTicketHoldingsAsync(userId, ct)).Tickets.Count,
            SignIns = info.AllVerifiedEmails
                .Select(e => googleAddresses.Contains(e) ? $"{e} (Google)" : e)
                .ToList(),
            Created = info.CreatedAt.ToDateTimeUtc(),
            LastSignIn = info.LastSignInAt?.ToDateTimeUtc(),
        };
    }
}

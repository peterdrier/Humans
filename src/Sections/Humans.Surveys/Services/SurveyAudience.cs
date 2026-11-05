using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Surveys.Domain;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyAudience(
    ITeamService teamService,
    IUserService userService,
    IGovernanceService governanceService,
    ITicketService ticketService,
    IShiftView shiftView,
    ILogger logger) : IApplicationService
{
    /// <summary>
    /// Resolves an audience predicate into the set of recipient user ids via cross-section read
    /// interfaces. No marketing opt-out filter — surveys are System/always-send.
    /// </summary>
    internal async Task<IReadOnlySet<Guid>> ResolveRecipientIdsAsync(
        SurveyAudienceType type, Guid? teamId, Instant? loggedInSince, CancellationToken ct)
    {
        switch (type)
        {
            case SurveyAudienceType.Team:
                {
                    if (teamId is null) return new HashSet<Guid>();
                    var team = await teamService.GetTeamAsync(teamId.Value, ct);
                    return team?.Members.Select(m => m.UserId).ToHashSet() ?? new HashSet<Guid>();
                }

            case SurveyAudienceType.AllActiveMembers:
                return (await ActiveMemberIdsAsync(ct)).ToHashSet();

            case SurveyAudienceType.Asociados:
                {
                    var tiers = await governanceService.GetMembershipTiersAsync(ct);
                    return (await userService.GetAllUserInfosAsync(ct))
                        .Where(user => IsEligibleAsociado(user)
                            && tiers.GetValueOrDefault(user.Id) == MembershipTier.Asociado)
                        .Select(user => user.Id)
                        .ToHashSet();
                }

            case SurveyAudienceType.TicketHolders:
                {
                    var orders = await ticketService.GetTicketOrdersAsync(ct);
                    return orders
                        .Where(o => o.IsCurrentEvent)
                        .SelectMany(o => o.Attendees)
                        .Where(a => a.MatchedUserId.HasValue
                            && (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
                        .Select(a => a.MatchedUserId!.Value)
                        .ToHashSet();
                }

            case SurveyAudienceType.ShiftParticipants:
                {
                    var activeIds = await ActiveMemberIdsAsync(ct);
                    var views = await shiftView.GetUsersAsync(activeIds, ct);
                    return views
                        .Where(kv => kv.Value.HasShift)
                        .Select(kv => kv.Key)
                        .ToHashSet();
                }

            case SurveyAudienceType.LoggedInSince:
                {
                    // Null LastLoginAt never matches (predates tracking). Deliberately no IsApproved filter:
                    // mid-onboarding users belong in this audience. Deletion-pending, rejected and suspended
                    // accounts can't reach the survey, so they are never invited.
                    if (loggedInSince is null) return new HashSet<Guid>();
                    var users = await userService.GetAllUserInfosAsync(ct);
                    return users
                        .Where(u => u.LastLoginAt is { } lastLogin && lastLogin >= loggedInSince.Value)
                        .Where(u => !u.IsDeletionPending)
                        .Where(u => u.State is not (UserState.Rejected or UserState.Suspended or UserState.AdminSuspended))
                        .Select(u => u.Id)
                        .ToHashSet();
                }

            default:
                // Unknown audience type resolves to nobody, silently — the send would look like
                // it worked while inviting no one.
                logger.SwitchDefaultWarn(type);
                return new HashSet<Guid>();
        }
    }

    private async Task<List<Guid>> ActiveMemberIdsAsync(CancellationToken ct)
    {
        var users = await userService.GetAllUserInfosAsync(ct);
        return users
            .Where(u => u.State == UserState.Active && !u.IsFake && !u.IsDeletionPending)
            .Select(u => u.Id)
            .ToList();
    }

    /// <summary>The account half of Asociado eligibility; the tier half is Governance's
    /// (<see cref="IGovernanceService.GetMembershipTierAsync"/>). The Consent Coordinator's review
    /// does not gate voting.</summary>
    private static bool IsEligibleAsociado(UserInfo? user)
        => user is
        {
            IsGdprAnonymized: false,
            IsDeletionPending: false,
            IsMerged: false,
            State: UserState.Active,
        };

    internal async Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default)
    {
        // Every caller passes an id a draft, invitation or response is stored under. A merged-away
        // id resolves to an eligible survivor, but answering under the archived id would let one
        // Asociado hold two ballots, so eligibility requires the stored id to be the live one.
        var info = await userService.GetUserInfoAsync(userId, ct);
        return info is not null && info.Id == userId
            && IsEligibleAsociado(info)
            && await governanceService.GetMembershipTierAsync(userId, ct) == MembershipTier.Asociado;
    }

}

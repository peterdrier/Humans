using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteRosterBuilder(
    IApplicationRepository applications,
    IRoleAssignmentService roleAssignments,
    ITeamService teams,
    IUserService users,
    IClock clock) : IApplicationService
{
    /// <summary>
    /// Snapshots who may vote, at this instant, from Governance's own data: active Approved
    /// Asociado terms and the active Board role holders as official, plus the configured
    /// indicative audience.
    /// </summary>
    /// <remarks>
    /// Board members are de facto Asociados and sit on the official roster whatever their
    /// profile tier says. A person reachable through more than one source gets exactly one
    /// row, official if any source made them official.
    /// </remarks>
    internal async Task<List<AssemblyVoteRoster>> BuildRosterAsync(
        AssemblyVote vote, CancellationToken ct)
    {
        var today = clock.GetCurrentInstant().InUtc().Date;

        var asociados = await applications.GetActiveApprovedTierUserIdsAsync(
            MembershipTier.Asociado, today, ct);
        var board = await roleAssignments.GetActiveUserIdsInRoleAsync(RoleNames.Board, ct);

        var tiers = new Dictionary<Guid, MembershipTier>();
        var official = new HashSet<Guid>();
        var indicative = new HashSet<Guid>();
        var boardMembers = board.ToHashSet();

        foreach (var id in asociados)
        {
            official.Add(id);
            tiers[id] = MembershipTier.Asociado;
        }

        foreach (var id in boardMembers)
        {
            official.Add(id);
            tiers.TryAdd(id, MembershipTier.Asociado);
        }

        if (vote.IndicativeAudience is IndicativeAudience.Colaboradores or IndicativeAudience.AllMembers)
        {
            var colaboradores = await applications.GetActiveApprovedTierUserIdsAsync(
                MembershipTier.Colaborador, today, ct);
            foreach (var id in colaboradores.Where(id => !official.Contains(id)))
            {
                indicative.Add(id);
                tiers.TryAdd(id, MembershipTier.Colaborador);
            }
        }

        if (vote.IndicativeAudience == IndicativeAudience.AllMembers)
        {
            var volunteers = await teams.GetTeamAsync(SystemTeamIds.Volunteers, ct);
            foreach (var member in volunteers?.Members ?? [])
            {
                if (official.Contains(member.UserId) || indicative.Contains(member.UserId)) continue;
                indicative.Add(member.UserId);
                tiers.TryAdd(member.UserId, MembershipTier.Volunteer);
            }
        }

        // A suspended or deleted account keeps no entitlement: the roster is who could vote
        // when it was frozen, and an inactive account could not.
        var candidates = official.Concat(indicative).ToList();
        var infos = await users.GetUserInfosAsync(candidates, ct);

        return candidates
            .Where(id => infos.TryGetValue(id, out var info) && info.State == UserState.Active)
            .Select(id => new AssemblyVoteRoster
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                UserId = id,
                Tier = tiers.GetValueOrDefault(id, MembershipTier.Volunteer),
                IsBoardMember = boardMembers.Contains(id),
                IsOfficial = official.Contains(id)
            })
            .ToList();
    }
}

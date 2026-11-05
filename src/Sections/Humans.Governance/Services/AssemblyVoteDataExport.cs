using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Users.Contracts;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteDataExport(
    IAssemblyVoteRepository repository,
    IUserService users) : IApplicationService
{
    internal const string AssemblyVotes = "AssemblyVotes";
    internal const string AssemblyVoteActions = "AssemblyVoteActions";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);

        // Plus every account merged into this one: the merge leaves roster rows and ballots on
        // the merged-away id, and the erasure path follows the same chain.
        IReadOnlyList<(AssemblyVoteRoster Roster, AssemblyVote Vote, AssemblyBallot? Ballot)> record = [];
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            record = [.. record, .. await repository.GetVotingRecordForUserAsync(id, ct)];
        }

        var rows = record
            .Select(x => new
            {
                Vote = x.Vote.Title.Resolve(x.Vote.OfficialCulture, x.Vote.OfficialCulture),
                x.Vote.Status,
                ClosesAt = x.Vote.ClosesAt.ToIso8601(),
                Entitlement = x.Roster.IsOfficial ? "Official" : "Indicative",
                x.Roster.Tier,
                x.Roster.IsBoardMember,
                Ballot = x.Ballot is null
                    ? null
                    : new
                    {
                        x.Ballot.Choice,
                        x.Ballot.Ranking,
                        x.Ballot.Revision,
                        CastAt = x.Ballot.CastAt.ToIso8601(),
                        UpdatedAt = x.Ballot.UpdatedAt.ToIso8601(),
                        History = x.Ballot.History
                            .OrderBy(h => h.Revision)
                            .Select(h => new
                            {
                                h.Revision,
                                h.Choice,
                                h.Ranking,
                                RecordedAt = h.RecordedAt.ToIso8601()
                            })
                            .ToList()
                    }
            })
            .ToList();

        // The survivor's id alone, not AllUserIds — the mirror image of the roster read above.
        // ReassignAsync moves the actor columns onto the survivor at merge time, so a merged-away
        // id owns no actor rows and asking with the raw id returns an empty slice.
        var actorId = info?.Id ?? userId;
        var (acted, peeks) = await repository.GetActorRecordForUserAsync(actorId, ct);

        var actions = acted
            .Select(v => new
            {
                Vote = v.Title.Resolve(v.OfficialCulture, v.OfficialCulture),
                v.Status,
                Roles = new[]
                    {
                        v.CreatedByUserId == actorId ? "Drafted" : null,
                        v.OpenedByUserId == actorId ? "Opened" : null,
                        v.ClosedByUserId == actorId ? "Closed" : null
                    }
                    .OfType<string>()
                    .ToList(),
                CreatedAt = v.CreatedAt.ToIso8601(),
                OpenedAt = v.OpenedAt.ToIso8601(),
                ClosedAt = v.ClosedAt.ToIso8601()
            })
            .ToList();

        var peekRows = peeks
            .Select(x => new
            {
                Vote = x.Vote.Title.Resolve(x.Vote.OfficialCulture, x.Vote.OfficialCulture),
                PeekedAt = x.Peek.PeekedAt.ToIso8601()
            })
            .ToList();

        return
        [
            new UserDataSlice(AssemblyVotes, rows),
            new UserDataSlice(AssemblyVoteActions,
                new { RanVotes = actions, Peeks = peekRows })
        ];
    }

}

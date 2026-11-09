using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteResultReader(
    IAssemblyVoteRepository repository,
    IUserService users,
    IClock clock) : IApplicationService
{
    /// <summary>
    /// Counts the vote's ballots into a storable result: the official audience always, the
    /// indicative audience separately when the vote has one. The two are never merged.
    /// </summary>
    internal async Task<AssemblyVoteResult> ComputeResultAsync(AssemblyVote vote, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(vote.Id, ct);
        var ballots = await repository.GetBallotsAsync(vote.Id, ct);
        var byRoster = ballots.ToDictionary(b => b.RosterId);

        var official = Tally(vote, roster.Where(r => r.IsOfficial).ToList(), byRoster);
        var indicative = vote.IndicativeAudience == IndicativeAudience.None
            ? null
            : Tally(vote, roster.Where(r => !r.IsOfficial).ToList(), byRoster);

        return new AssemblyVoteResult(
            official,
            indicative,
            vote.Kind == AssemblyVoteKind.YesNo
                ? AssemblyVoteMethod.YesNo
                : AssemblyVoteMethod.InstantRunoff,
            clock.GetCurrentInstant());
    }

    private static AssemblyVoteAudienceResult Tally(
        AssemblyVote vote,
        IReadOnlyList<AssemblyVoteRoster> audience,
        Dictionary<Guid, AssemblyBallot> ballotsByRoster)
    {
        var cast = audience
            .Select(r => ballotsByRoster.GetValueOrDefault(r.Id))
            .OfType<AssemblyBallot>()
            .Select(b => new AssemblyVoteCounting.CountableBallot(b.Choice, b.Ranking))
            .ToList();

        if (vote.Kind == AssemblyVoteKind.YesNo)
        {
            var (tally, verdict) = AssemblyVoteCounting.CountYesNo(cast, vote.RequiredMajority);
            return new AssemblyVoteAudienceResult(
                audience.Count, cast.Count, tally, null, null, verdict, []);
        }

        var (rounds, winner, rankedVerdict, notes) = AssemblyVoteCounting.CountInstantRunoff(
            cast, vote.Options.ToList());

        return new AssemblyVoteAudienceResult(
            audience.Count, cast.Count, null, rounds, winner, rankedVerdict, notes);
    }

    /// <summary>Every standing ballot with its voter's name, for a disclosed results view.</summary>
    internal async Task<IReadOnlyList<AssemblyBallotDisclosureRow>> DisclosureRowsAsync(
        Guid voteId, CancellationToken ct)
    {
        var roster = await repository.GetRosterAsync(voteId, ct);
        var ballots = await repository.GetBallotsAsync(voteId, ct);
        var byRoster = ballots.ToDictionary(b => b.RosterId);

        var userIds = roster.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList();
        var infos = await users.GetUserInfosAsync(userIds, ct);

        return roster
            .Select(r => (Roster: r, Ballot: byRoster.GetValueOrDefault(r.Id)))
            .Where(x => x.Ballot is not null)
            .Select(x => new AssemblyBallotDisclosureRow(
                x.Roster.UserId,
                x.Roster.UserId is { } id && infos.TryGetValue(id, out var info)
                    ? info.BurnerName
                    // An erased roster row keeps its ballot but loses its name — the count
                    // stays right and the person is gone, which is what Art. 17 asks for.
                    : "—",
                x.Roster.IsOfficial,
                x.Ballot!.Choice,
                x.Ballot.Ranking,
                x.Ballot.Revision))
            .ToList();
    }

    internal async Task<IReadOnlyList<AssemblyVotePeekView>> PeekViewsAsync(
        Guid voteId, CancellationToken ct)
    {
        var peeks = await repository.GetPeeksAsync(voteId, ct);
        if (peeks.Count == 0) return [];

        return peeks
            .Select(p => new AssemblyVotePeekView(
                p.AdminUserId,
                p.PeekedAt))
            .ToList();
    }

    /// <summary>
    /// The name of the admin who stopped the vote, for the acta. Null when the vote lapsed
    /// at its announced time (nobody closed it) or when the account no longer resolves —
    /// the acta says "an administrator" then rather than inventing a name.
    /// </summary>
    internal async Task<string?> ClosedByNameAsync(AssemblyVote vote, CancellationToken ct)
    {
        if (vote.ClosedByUserId is not { } closerId) return null;
        var infos = await users.GetUserInfosAsync([closerId], ct);
        return infos.TryGetValue(closerId, out var info) ? info.BurnerName : null;
    }
}

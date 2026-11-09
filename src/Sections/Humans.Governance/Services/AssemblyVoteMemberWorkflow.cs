using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteMemberWorkflow(
    IAssemblyVoteRepository repository,
    AssemblyVoteSettlement settlement,
    IUserService users,
    IAuditLogService audit,
    AssemblyVoteEffects effects,
    INotificationAutoResolve notificationResolve,
    IClock clock) : IApplicationService
{
    internal async Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default)
    {
        var votes = await settlement.SettleAllAsync(ct);
        var items = new List<AssemblyVoteListItem>(votes.Count);

        // Members never see a draft. Chain ids are resolved once for the whole list: a member
        // with no roster row of their own is the common case, and exactly the one that has to ask.
        var chainIds = (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

        foreach (var vote in votes.Where(v => v.Status != AssemblyVoteStatus.Draft))
        {
            var roster = await EffectiveRosterAsync(vote.Id, userId, ct, chainIds);
            var ballot = roster is null
                ? null
                : await repository.GetBallotForRosterAsync(vote.Id, roster.Id, ct);

            items.Add(new AssemblyVoteListItem(
                vote.Id,
                Resolve(vote.Title, vote.OfficialCulture),
                vote.Status,
                vote.Kind,
                vote.ClosesAt,
                vote.ClosedAt,
                IsOnRoster: roster is not null,
                IsOfficial: roster?.IsOfficial ?? false,
                HasBallot: ballot is not null,
                await repository.GetParticipationAsync(vote.Id, ct)));
        }

        return items;
    }

    internal async Task<AssemblyVoteDetail?> GetVoteForMemberAsync(
        Guid voteId, Guid userId, CancellationToken ct = default)
    {
        var vote = await settlement.SettleAsync(voteId, ct);
        if (vote is null) return null;
        // Same rule as the list: a draft's id is not a back door to its official text.
        if (vote.Status == AssemblyVoteStatus.Draft) return null;

        var roster = await EffectiveRosterAsync(voteId, userId, ct);
        var ballot = roster is null
            ? null
            : await repository.GetBallotForRosterAsync(voteId, roster.Id, ct);

        return await DetailAsync(vote, roster, ballot, ct);
    }

    /// <summary>
    /// Builds the page model shared by the vote page and the results page. Carries the
    /// viewer's own ballot but never anyone else's, and never a tally.
    /// </summary>
    internal async Task<AssemblyVoteDetail> DetailAsync(
        AssemblyVote vote,
        AssemblyVoteRoster? roster,
        AssemblyBallot? ballot,
        CancellationToken ct)
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        var now = clock.GetCurrentInstant();

        return new AssemblyVoteDetail(
            vote.Id,
            Resolve(vote.Title, vote.OfficialCulture),
            Resolve(vote.OfficialText, vote.OfficialCulture),
            vote.OfficialCulture,
            // Only when the viewer's culture was actually authored: Resolve falls back to the
            // official text, and labelling the binding text "a translation" would be wrong.
            IsTranslation: vote.OfficialText.HasCulture(culture)
                           && !culture.Equals(vote.OfficialCulture, StringComparison.OrdinalIgnoreCase),
            AssemblyVoteDrafts.SafeInfoUrl(vote.InfoUrl),
            vote.Kind,
            vote.RequiredMajority,
            vote.IndicativeAudience,
            vote.BallotDisclosure,
            vote.Status,
            vote.ClosesAt,
            vote.ClosedAt,
            vote.AssemblyDate,
            vote.CancelReason,
            IsOnRoster: roster is not null,
            IsOfficial: roster?.IsOfficial ?? false,
            CanCastBallot: roster is not null && vote.AcceptsBallotsAt(now),
            vote.Options
                .Select(o => new AssemblyVoteOptionView(
                    o.Key, Resolve(o.Label, vote.OfficialCulture), o.Order))
                .ToList(),
            ballot is null ? null : BallotView(ballot),
            await repository.GetParticipationAsync(vote.Id, ct));
    }

    private static AssemblyBallotView BallotView(AssemblyBallot ballot) =>
        new(ballot.Choice,
            ballot.Ranking,
            ballot.Revision,
            ballot.CastAt,
            ballot.UpdatedAt,
            ballot.History
                .OrderBy(h => h.Revision)
                .Select(h => new AssemblyBallotRevisionView(
                    h.Revision, h.Choice, h.Ranking, h.RecordedAt))
                .ToList());

    internal async Task<BallotSubmissionOutcome> CastBallotAsync(
        Guid voteId,
        Guid userId,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        CancellationToken ct = default)
    {
        var vote = await settlement.SettleAsync(voteId, ct);
        if (vote is null) return BallotSubmissionOutcome.NotFound;

        var now = clock.GetCurrentInstant();
        if (!vote.AcceptsBallotsAt(now)) return BallotSubmissionOutcome.VoteNotOpen;

        // Entitlement comes from the frozen roster, never from the member's tier today: a
        // member who lost their term mid-vote keeps the ballot the roster granted them.
        var roster = await EffectiveRosterAsync(voteId, userId, ct);
        if (roster is null) return BallotSubmissionOutcome.NotOnRoster;

        if (!TryNormalizeBallot(vote, choice, ranking, out var normalizedRanking))
        {
            return BallotSubmissionOutcome.InvalidBallot;
        }

        var ballot = await repository.UpsertBallotAsync(
            voteId, roster.Id, choice, normalizedRanking, now, ct);

        // The vote closed while this submission was in flight. Nothing was written, and the
        // member is told so rather than being shown a "recorded" they would not find in the
        // published result.
        if (ballot is null) return BallotSubmissionOutcome.VoteNotOpen;

        // First cast vs change comes from the revision the locked upsert assigned: two first
        // ballots can both read none. Neither entry records the choice, and the entity is the vote,
        // never the ballot — audit rows outlive erasure, so a ballot id would re-link the person.
        await audit.LogAsync(
            ballot.Revision == 1 ? GovernanceAuditActions.AssemblyBallotCast : GovernanceAuditActions.AssemblyBallotChanged,
            AuditEntityTypes.AssemblyVote,
            voteId,
            ballot.Revision == 1
                ? $"Cast a ballot on assembly vote {voteId} (revision {ballot.Revision})."
                : $"Changed their ballot on assembly vote {voteId} (revision {ballot.Revision}).",
            userId);

        await effects.AfterTransitionAsync(
            "resolving the voter's open-vote notification", voteId,
            token => notificationResolve.ResolveBySourceAsync(
                userId, GovernanceNotificationSources.AssemblyVoteOpened, token, sourceKey: voteId.ToString()));

        return BallotSubmissionOutcome.Recorded;
    }

    /// <summary>
    /// Validates a submitted ballot against the vote's kind and options, and reduces a
    /// ranked ballot to the ordered key list the counting rules expect.
    /// </summary>
    /// <remarks>
    /// Rejects a choice that does not belong to the vote's kind, an unknown option key, and
    /// a duplicate key. A partial ranking is accepted — unranked options are simply never
    /// preferred — and so is an Abstain on a ranked vote.
    /// </remarks>
    private static bool TryNormalizeBallot(
        AssemblyVote vote,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        out IReadOnlyList<string>? normalized)
    {
        normalized = null;

        if (vote.Kind == AssemblyVoteKind.YesNo)
        {
            return choice is AssemblyBallotChoice.Yes
                or AssemblyBallotChoice.No
                or AssemblyBallotChoice.Abstain;
        }

        if (choice == AssemblyBallotChoice.Abstain) return true;
        if (choice != AssemblyBallotChoice.Ranked) return false;
        if (ranking is null || ranking.Count == 0) return false;

        var keys = vote.Options.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        if (ranking.Any(k => !keys.Contains(k))) return false;
        if (ranking.Distinct(StringComparer.Ordinal).Count() != ranking.Count) return false;

        normalized = ranking;
        return true;
    }

    internal static string Resolve(GovernanceLocalizedText text, string officialCulture) =>
        text.Resolve(CultureInfo.CurrentUICulture.Name, officialCulture);

    /// <summary>
    /// The roster row entitling this human: their own, or one left on an account merged into
    /// theirs. A row already carrying a ballot wins, so a human enrolled twice amends their ballot
    /// rather than casting a second; two ballot-bearing rows resolve to the first by id.
    /// </summary>
    internal async Task<AssemblyVoteRoster?> EffectiveRosterAsync(
        Guid voteId, Guid userId, CancellationToken ct, IReadOnlyList<Guid>? chainIds = null)
    {
        // Every id the human has held: the resolved record's own id first and the archived
        // ids after it in id order, not set enumeration order, which comes out of a cache
        // and is not stable, and which row a member is shown must not vary between reads.
        chainIds ??= (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        if (chainIds.Count == 1) return await repository.GetRosterRowAsync(voteId, chainIds[0], ct);

        var candidates = new List<AssemblyVoteRoster>(chainIds.Count);
        foreach (var id in chainIds)
        {
            if (await repository.GetRosterRowAsync(voteId, id, ct) is { } row)
            {
                candidates.Add(row);
            }
        }

        if (candidates.Count <= 1) return candidates.FirstOrDefault();

        foreach (var candidate in candidates)
        {
            if (await repository.GetBallotForRosterAsync(voteId, candidate.Id, ct) is not null)
            {
                return candidate;
            }
        }

        return candidates[0];
    }

}

using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Constants;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteAdministration(
    IAssemblyVoteRepository repository,
    AssemblyVoteRosterBuilder rosterBuilder,
    AssemblyVoteSettlement settlement,
    AssemblyVoteEffects effects,
    IAuditLogService audit,
    IClock clock) : IApplicationService
{
    private const int MaxCancelReasonLength = 4000; // governance_assembly_votes.CancelReason

    internal async Task<AssemblyVoteActionResult> OpenAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Draft) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        if (vote.ClosesAt <= now) return AssemblyVoteActionResult.Invalid;

        var roster = await rosterBuilder.BuildRosterAsync(vote, ct);
        if (roster.Count == 0) return AssemblyVoteActionResult.Invalid;

        // Building the roster reads three other sections, so the announced deadline can pass
        // while it runs. A vote that opens already lapsed is open for no time at all: the next
        // sweep closes it, having given its electorate no chance to vote.
        now = clock.GetCurrentInstant();
        if (vote.ClosesAt <= now) return AssemblyVoteActionResult.Invalid;

        vote.Status = AssemblyVoteStatus.Open;
        vote.OpenedAt = now;
        vote.OpenedByUserId = adminUserId;
        // UpdatedAt is deliberately left as read: the repository compares it against the
        // persisted row to tell a draft edit that landed while this roster was being built
        // from this open, and stamps it once the open is accepted.

        // Refused means somebody else opened this vote while its roster was being built.
        // Theirs is the roster that counts, and nothing below should run a second time.
        if (!await repository.OpenWithRosterAsync(vote, roster, ct))
            return AssemblyVoteActionResult.WrongState;

        var officialCount = roster.Count(r => r.IsOfficial);
        await effects.AfterTransitionAsync(
            "auditing the vote opening", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteOpened, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Opened assembly vote {vote.Id}: {officialCount} official and "
                + $"{roster.Count - officialCount} indicative roster members.",
                adminUserId));

        await effects.AfterTransitionAsync(
            "notifying the roster that the vote opened", vote.Id,
            token => effects.NotifyRosterOpenedAsync(vote, roster, token));
        return AssemblyVoteActionResult.Ok;
    }

    internal async Task<AssemblyVoteActionResult> StopAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default)
    {
        // Settle first: past ClosesAt the vote is already closed, and stopping it then would
        // attribute a lapse to the admin who happened to click.
        var vote = await settlement.SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        // Refused means the vote reached a terminal state first — its own lapse, or another
        // admin's stop. The vote is closed either way, but not by this request.
        if (!await settlement.CloseAsync(vote, adminUserId, GovernanceAuditActions.AssemblyVoteStopped,
                $"Stopped assembly vote {vote.Id} before its announced closing time.", ct))
        {
            return AssemblyVoteActionResult.WrongState;
        }

        return AssemblyVoteActionResult.Ok;
    }

    internal async Task<AssemblyVoteActionResult> ExtendAsync(
        Guid voteId, Instant newClosesAt, Guid adminUserId, CancellationToken ct = default)
    {
        var vote = await settlement.SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        // Only later. Shortening a vote the electorate has already been given a deadline for
        // is what Stop is for, and Stop is explicit about being a stop.
        if (newClosesAt <= vote.ClosesAt) return AssemblyVoteActionResult.Invalid;

        var previous = vote.ClosesAt;
        // The revision this extension was built from: two concurrent Extends must not let the
        // shorter commit second and move the electorate's deadline backwards.
        var readAt = vote.UpdatedAt;
        var extendedAt = clock.GetCurrentInstant();
        vote.ClosesAt = newClosesAt;
        vote.UpdatedAt = extendedAt;

        // The vote closed, or was extended by somebody else, between the read above and this
        // write. Nothing was extended, and "only an open vote can be extended" is the honest
        // answer: the admin re-reads the new deadline and extends from that.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, readAt, ct))
            return AssemblyVoteActionResult.WrongState;

        await effects.AfterTransitionAsync(
            "auditing the extension", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteExtended, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Extended assembly vote {vote.Id} from {previous} to {newClosesAt}.",
                adminUserId));

        // Re-arm the T-24h reminder: stamps up to now mean "told about the old deadline"; later
        // stamps come from a sweep already announcing the new one. Post-transition, not on the
        // request token: there is no replay for an extension.
        await effects.AfterTransitionAsync(
            "re-arming the reminder after the extension", vote.Id,
            token => repository.ClearReminderStampsAsync(vote.Id, extendedAt, token));

        return AssemblyVoteActionResult.Ok;
    }

    internal async Task<AssemblyVoteActionResult> CancelAsync(
        Guid voteId, string reason, Guid adminUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AssemblyVoteActionResult.Invalid;
        // Its own column is varchar(4000); over that the update dies on the insert, which is
        // a 500 where "that cancellation was rejected" is the honest answer.
        if (!AssemblyVoteDrafts.Fits(reason, MaxCancelReasonLength)) return AssemblyVoteActionResult.Invalid;

        // Settle first: a vote whose deadline has passed is Closed with its result stored,
        // and cancelling it then would erase that result and the decision it recorded.
        var vote = await settlement.SettleAsync(voteId, ct);
        if (vote is null) return AssemblyVoteActionResult.NotFound;
        if (vote.Status != AssemblyVoteStatus.Open) return AssemblyVoteActionResult.WrongState;

        var now = clock.GetCurrentInstant();
        vote.Status = AssemblyVoteStatus.Cancelled;
        vote.ClosedAt = now;
        vote.ClosedByUserId = adminUserId;
        vote.CancelReason = reason;
        vote.UpdatedAt = now;

        // No result is computed: a cancelled vote decided nothing, and the ballots stay only
        // as a record that it was attempted. A refused write means the vote closed first,
        // and a closed vote is never cancelled out of its result.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, ct: ct))
            return AssemblyVoteActionResult.WrongState;

        await effects.ClearOpenNotificationAsync(vote, adminUserId, ct);

        await effects.AfterTransitionAsync(
            "auditing the vote cancellation", vote.Id,
            _ => audit.LogAsync(
                GovernanceAuditActions.AssemblyVoteCancelled, AuditEntityTypes.AssemblyVote, vote.Id,
                $"Cancelled assembly vote {vote.Id}: {reason}",
                adminUserId));

        await effects.AfterTransitionAsync(
            "notifying the roster that the vote was cancelled", vote.Id,
            token => effects.NotifyRosterCancelledAsync(vote, token));
        return AssemblyVoteActionResult.Ok;
    }

}

using System.Text.Json;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services;

internal sealed class AssemblyVoteSettlement(
    IAssemblyVoteRepository repository,
    IAuditLogService audit,
    IClock clock,
    AssemblyVoteEffects effects,
    AssemblyVoteResultReader resultReader,
    JsonSerializerOptions resultJsonOptions,
    string lapseJobName) : IApplicationService
{
    /// <summary>
    /// How long a Closed vote with no stored tally is left alone before a read treats it as
    /// interrupted and finishes it. Long enough that a close still running its own tail is
    /// never mistaken for a dead one; short enough that a real interruption is repaired by
    /// the next read or the next hourly sweep.
    /// </summary>
    private static readonly Duration InterruptedCloseGrace = Duration.FromMinutes(5);

    /// <summary>
    /// Loads a vote and closes it first if its deadline has passed, so no caller can ever
    /// observe an Open vote that should be Closed.
    /// </summary>
    public async Task<AssemblyVote?> SettleAsync(Guid voteId, CancellationToken ct)
    {
        var vote = await repository.GetByIdAsync(voteId, ct);
        if (vote is null) return null;

        return await SettleOneAsync(vote, ct);
    }

    /// <summary>
    /// Brings one already-loaded vote up to date: closes it if its deadline has passed, and
    /// finishes a close that was interrupted after its status went down. Returns the vote as
    /// it now stands — which is not always the one passed in, because a close that loses to
    /// somebody else's transition leaves this object stamped Closed in memory only.
    /// </summary>
    private async Task<AssemblyVote> SettleOneAsync(AssemblyVote vote, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (vote.Status != AssemblyVoteStatus.Open || now < vote.ClosesAt)
        {
            await FinishInterruptedCloseAsync(vote, ct);
            return vote;
        }

        if (await CloseAsync(vote, closedByUserId: null, GovernanceAuditActions.AssemblyVoteClosed,
                $"Assembly vote {vote.Id} closed automatically at its announced time.", ct))
        {
            return vote;
        }

        // Refused: an Extend moved the deadline or another close landed first. CloseAsync already
        // stamped this object Closed, so the row, not this object, is the truth.
        return await repository.GetByIdAsync(vote.Id, ct) ?? vote;
    }

    /// <summary>
    /// Closure writes the status first and everything else second (see <see cref="CloseAsync"/>).
    /// A process that died in between left a vote that is closed but has no result, no retired
    /// notification and no audit entry; whoever looks next finishes all three. Who closed it is
    /// already persisted, so the replay attributes it exactly as the original would have.
    /// </summary>
    public async Task FinishInterruptedCloseAsync(AssemblyVote vote, CancellationToken ct)
    {
        if (vote.Status != AssemblyVoteStatus.Closed || vote.ResultJson is not null) return;

        // A close that committed its status seconds ago is still running its own tail; only one
        // that has stood past the grace period is taken over, or the audit entry would duplicate.
        var readAt = vote.UpdatedAt;
        if (clock.GetCurrentInstant() < readAt + InterruptedCloseGrace) return;

        // And two readers arriving after that are still two. Bumping UpdatedAt from the value
        // each of them read is the claim: exactly one write lands, and the loser leaves the
        // tail to the winner.
        vote.UpdatedAt = clock.GetCurrentInstant();
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, readAt, ct)) return;

        await FinishCloseAsync(
            vote,
            vote.ClosedByUserId,
            vote.ClosedByUserId is null ? GovernanceAuditActions.AssemblyVoteClosed : GovernanceAuditActions.AssemblyVoteStopped,
            vote.ClosedByUserId is null
                ? $"Assembly vote {vote.Id} closed automatically at its announced time."
                : $"Stopped assembly vote {vote.Id} before its announced closing time.",
            ct);
    }

    public async Task<IReadOnlyList<AssemblyVote>> SettleAllAsync(CancellationToken ct)
    {
        var votes = await repository.GetAllAsync(ct);

        var settled = new List<AssemblyVote>(votes.Count);
        foreach (var vote in votes)
        {
            settled.Add(await SettleOneAsync(vote, ct));
        }

        return settled;
    }

    /// <summary>
    /// The single close path: computes the result once, stores it on the vote, resolves the
    /// open-vote notification, and audits. Every way a vote can close funnels through here
    /// so a closed vote always has exactly one stored result.
    /// </summary>
    public async Task<bool> CloseAsync(
        AssemblyVote vote,
        Guid? closedByUserId,
        AuditActionDefinition action,
        string description,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();

        vote.Status = AssemblyVoteStatus.Closed;
        // A lapsed vote closed at its announced time, whenever it was noticed; the acta prints
        // ClosedAt into the legal summary. An Admin Stop really does close it now.
        vote.ClosedAt = closedByUserId is null ? vote.ClosesAt : now;
        vote.ClosedByUserId = closedByUserId;
        vote.UpdatedAt = now;

        // The status is persisted *before* the ballots are read: from that commit on,
        // UpsertBallotAsync refuses, so every accepted ballot is in the table when counting looks.
        // Refused means another actor closed or extended first and owns the whole tail.
        if (!await repository.UpdateAsync(vote, AssemblyVoteStatus.Open, ct: ct)) return false;

        await FinishCloseAsync(vote, closedByUserId, action, description, ct);
        return true;
    }

    /// <summary>
    /// Everything a close owes after its status is down; <see cref="SettleAsync"/> replays it. The
    /// stored result is written last because its absence is what the replay looks for: the worst a
    /// crash costs is a duplicate close entry, never a missing one.
    /// </summary>
    private async Task FinishCloseAsync(
        AssemblyVote vote,
        Guid? closedByUserId,
        AuditActionDefinition action,
        string description,
        CancellationToken ct)
    {
        await effects.ClearOpenNotificationAsync(vote, closedByUserId, ct);

        if (closedByUserId is { } actor)
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, actor);
        }
        else
        {
            await audit.LogAsync(action, AuditEntityTypes.AssemblyVote, vote.Id, description, lapseJobName);
        }

        await StoreResultAsync(vote, ct);
    }

    /// <summary>Stores the tally on the already-closed vote; also the settle path's repair.</summary>
    private async Task StoreResultAsync(AssemblyVote vote, CancellationToken ct)
    {
        var result = await resultReader.ComputeResultAsync(vote, ct);
        vote.ResultJson = JsonSerializer.Serialize(result, resultJsonOptions);

        // Closed is the state this write expects and the one it leaves: the tally lands on the
        // row the close already stamped, and a vote cancelled out from under it gets nothing.
        await repository.UpdateAsync(vote, AssemblyVoteStatus.Closed, ct: ct);
    }

}

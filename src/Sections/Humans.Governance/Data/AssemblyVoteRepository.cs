using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;

namespace Humans.Governance.Data;

/// <summary>
/// EF-backed implementation of <see cref="IAssemblyVoteRepository"/>. The only non-test file
/// that touches <c>DbContext.AssemblyVotes</c>, <c>AssemblyVoteOptions</c>,
/// <c>AssemblyVoteRosterEntries</c>, <c>AssemblyBallots</c>, <c>AssemblyBallotHistories</c>,
/// or <c>AssemblyVotePeeks</c>.
/// </summary>
internal sealed class AssemblyVoteRepository(IDbContextFactory<GovernanceDbContext> factory)
    : IAssemblyVoteRepository
{
    // ==========================================================================
    // Votes
    // ==========================================================================

    public Task<AssemblyVote?> GetByIdAsync(Guid voteId, CancellationToken ct = default) =>
        WithContextAsync(ctx => ctx.AssemblyVotes
            .Include(v => v.Options)
            .FirstOrDefaultAsync(v => v.Id == voteId, ct), ct);

    public async Task<IReadOnlyList<AssemblyVote>> GetAllAsync(CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyVotes
            .AsNoTracking()
            .Include(v => v.Options)
            .ToListAsync(ct), ct);

    public async Task AddAsync(AssemblyVote vote, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AssemblyVotes.Add(vote);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateAsync(
        AssemblyVote vote, AssemblyVoteStatus expectedStatus, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, vote.Id, ct);

        var persisted = await ctx.AssemblyVotes.AsNoTracking()
            .Where(v => v.Id == vote.Id)
            .Select(v => new { v.Status, v.ClosesAt })
            .FirstOrDefaultAsync(ct);
        if (persisted is null) return false;

        // Every lifecycle write arrives as a snapshot the caller read earlier, with every
        // property marked modified, so a write built before somebody else's transition would
        // silently undo it. The caller says which state it read; if the row has moved on —
        // including from Open to the very state this write is trying to set, which is how two
        // Stops or a Stop racing the lapse both think they closed the vote — the write does
        // not happen and the caller is told so.
        if (persisted.Status != expectedStatus) return false;

        // An automatic close decided from the deadline it read. If the deadline has moved
        // later since, an Extend committed in between and is the newer fact: the vote is
        // still open, and this close is working from a deadline that no longer exists.
        if (vote.Status == AssemblyVoteStatus.Closed
            && vote.ClosedByUserId is null
            && persisted.ClosesAt > vote.ClosesAt)
        {
            return false;
        }

        ctx.AssemblyVotes.Update(vote);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Opens the transaction every write path to a vote row runs in, and takes that row's
    /// lock for its duration, so the state the write re-reads afterwards cannot change under
    /// it. Relational only: the in-memory provider this section's tests run on supports
    /// neither transactions nor raw SQL, and a single-process store has nothing to serialize
    /// against — there the write proceeds on the state check alone.
    /// </summary>
    private static async Task<IDbContextTransaction?> BeginLockedWriteAsync(
        GovernanceDbContext ctx, Guid voteId, CancellationToken ct)
    {
        if (!ctx.Database.IsRelational()) return null;

        var tx = await ctx.Database.BeginTransactionAsync(ct);
        await ctx.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM assembly_votes WHERE "Id" = {voteId} FOR UPDATE""", ct);
        return tx;
    }

    public async Task<bool> ReplaceOptionsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteOption> options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, vote.Id, ct);

        // Read under the lock, not from the caller's snapshot: an Open that committed since
        // the edit form was loaded makes this edit illegal, and the option rows are the part
        // of the content the ballot page is built from.
        var status = await ctx.AssemblyVotes.AsNoTracking()
            .Where(v => v.Id == vote.Id)
            .Select(v => (AssemblyVoteStatus?)v.Status)
            .FirstOrDefaultAsync(ct);
        if (status != AssemblyVoteStatus.Draft) return false;

        var existing = await ctx.AssemblyVoteOptions
            .Where(o => o.VoteId == vote.Id)
            .ToListAsync(ct);
        ctx.AssemblyVoteOptions.RemoveRange(existing);

        // `vote` arrives detached with the options its own load included. Those rows are
        // already tracked as `existing`, so leaving them on the graph would have Update()
        // try to track the same keys twice and throw before the save.
        vote.Options.Clear();
        ctx.AssemblyVotes.Update(vote);

        ctx.AssemblyVoteOptions.AddRange(options);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid voteId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, voteId, ct);

        // Status re-read under the lock, for the same reason every other write re-reads it:
        // a vote opened between the caller's check and here has a frozen roster and an
        // electorate that has been told to vote, and deleting it would take the vote away
        // from them with nothing left to show it existed.
        var vote = await ctx.AssemblyVotes.FirstOrDefaultAsync(v => v.Id == voteId, ct);
        if (vote is null || vote.Status != AssemblyVoteStatus.Draft) return false;

        ctx.AssemblyVotes.Remove(vote);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AssemblyVote>> GetLapsedOpenVotesAsync(
        Instant now, CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyVotes
            .AsNoTracking()
            .Include(v => v.Options)
            .Where(v => v.Status == AssemblyVoteStatus.Open && v.ClosesAt <= now)
            .ToListAsync(ct), ct);

    public async Task<IReadOnlyList<AssemblyVote>> GetOpenVotesClosingBetweenAsync(
        Instant from, Instant to, CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyVotes
            .AsNoTracking()
            .Include(v => v.Options)
            .Where(v => v.Status == AssemblyVoteStatus.Open && v.ClosesAt > from && v.ClosesAt <= to)
            .ToListAsync(ct), ct);

    // ==========================================================================
    // Roster
    // ==========================================================================

    public async Task<bool> OpenWithRosterAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, vote.Id, ct);

        var persisted = await ctx.AssemblyVotes.FirstOrDefaultAsync(v => v.Id == vote.Id, ct);
        if (persisted is null || persisted.Status != AssemblyVoteStatus.Draft) return false;

        // The roster was built from the caller's snapshot, and which members are on it comes
        // out of the draft — IndicativeAudience above all. A Board edit that committed while
        // those cross-section reads were running would open the vote with the new content and
        // the old electorate, which is the one mismatch a frozen roster can never be corrected
        // for. UpdatedAt moving means exactly that, so the open is refused and the caller
        // rebuilds the roster from the draft as it now stands.
        if (persisted.UpdatedAt != vote.UpdatedAt) return false;

        // Only the lifecycle fields are written, onto the row as it stands: the content is
        // identical to the caller's by the check above, and writing the whole snapshot back
        // would put this section one careless edit away from the bug that check prevents.
        persisted.Status = vote.Status;
        persisted.OpenedAt = vote.OpenedAt;
        persisted.OpenedByUserId = vote.OpenedByUserId;
        persisted.UpdatedAt = vote.OpenedAt ?? persisted.UpdatedAt;

        ctx.AssemblyVoteRosterEntries.AddRange(roster);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterAsync(
        Guid voteId, CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.VoteId == voteId)
            .ToListAsync(ct), ct);

    public Task<AssemblyVoteRoster?> GetRosterRowAsync(
        Guid voteId, Guid userId, CancellationToken ct = default) =>
        WithContextAsync(ctx => ctx.AssemblyVoteRosterEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.VoteId == voteId && r.UserId == userId, ct), ct);

    public async Task StampNotifiedAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, CancellationToken ct = default)
    {
        if (rosterIds.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.AssemblyVoteRosterEntries
            .Where(r => rosterIds.Contains(r.Id))
            .ToListAsync(ct);

        foreach (var row in rows)
            row.NotifiedAt = at;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task StampReminderSentAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, CancellationToken ct = default)
    {
        if (rosterIds.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.AssemblyVoteRosterEntries
            .Where(r => rosterIds.Contains(r.Id))
            .ToListAsync(ct);

        foreach (var row in rows)
            row.ReminderSentAt = at;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterNeedingReminderAsync(
        Guid voteId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.VoteId == voteId
                && r.ReminderSentAt == null
                && !ctx.AssemblyBallots.Any(b => b.RosterId == r.Id))
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Ballots
    // ==========================================================================

    /// <summary>
    /// Writes a member's ballot and its history row. Returns null when the vote is no longer
    /// accepting ballots.
    /// </summary>
    /// <remarks>
    /// The vote's state is re-read here under the row's lock, not taken from whatever the
    /// caller saw at the top of the request: a Stop or an automatic close can land in between,
    /// and a ballot accepted after the tally was taken would be told "recorded" while never
    /// appearing in the stored result. Closure persists the closed status before it reads any
    /// ballots, so that write contends for this same lock, and an accepted ballot is always
    /// one the count saw.
    /// </remarks>
    public async Task<AssemblyBallot?> UpsertBallotAsync(
        Guid voteId,
        Guid rosterId,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Lock the vote row before deciding, and hold it until the ballot is committed.
        // Closure persists the closed status first, and that UPDATE needs this same lock:
        // either it commits before this transaction — and the read below sees Closed and
        // refuses — or it waits behind this ballot, which is then already in the table when
        // counting reads it. Checking without the lock leaves the two free to interleave,
        // and a vote can be counted without a ballot it told the member it had recorded.
        await using var tx = await BeginLockedWriteAsync(ctx, voteId, ct);

        var vote = await ctx.AssemblyVotes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == voteId, ct);
        if (vote is null || !vote.AcceptsBallotsAt(now)) return null;

        var ballot = await ctx.AssemblyBallots
            .FirstOrDefaultAsync(b => b.VoteId == voteId && b.RosterId == rosterId, ct);

        if (ballot is null)
        {
            ballot = new AssemblyBallot
            {
                Id = Guid.NewGuid(),
                VoteId = voteId,
                RosterId = rosterId,
                Choice = choice,
                Ranking = ranking,
                Revision = 1,
                CastAt = now,
                UpdatedAt = now
            };
            ctx.AssemblyBallots.Add(ballot);
        }
        else
        {
            ballot.Revision++;
            ballot.Choice = choice;
            ballot.Ranking = ranking;
            ballot.UpdatedAt = now;
        }

        ctx.AssemblyBallotHistories.Add(new AssemblyBallotHistory
        {
            Id = Guid.NewGuid(),
            BallotId = ballot.Id,
            Revision = ballot.Revision,
            Choice = choice,
            Ranking = ranking,
            RecordedAt = now
        });

        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return ballot;
    }

    public Task<AssemblyBallot?> GetBallotForRosterAsync(
        Guid voteId, Guid rosterId, CancellationToken ct = default) =>
        WithContextAsync(ctx => ctx.AssemblyBallots
            .AsNoTracking()
            .Include(b => b.History)
            .FirstOrDefaultAsync(b => b.VoteId == voteId && b.RosterId == rosterId, ct), ct);

    public async Task<IReadOnlyList<AssemblyBallot>> GetBallotsAsync(
        Guid voteId, CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyBallots
            .AsNoTracking()
            .Where(b => b.VoteId == voteId)
            .ToListAsync(ct), ct);

    /// <summary>
    /// Aggregates over roster and ballot metadata only — <c>Choice</c>/<c>Ranking</c> never
    /// appear in the projection, which is what keeps this safe under the embargo.
    /// </summary>
    public async Task<AssemblyVoteParticipation> GetParticipationAsync(
        Guid voteId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var roster = ctx.AssemblyVoteRosterEntries.AsNoTracking().Where(r => r.VoteId == voteId);
        var ballots = ctx.AssemblyBallots.AsNoTracking().Where(b => b.VoteId == voteId);

        var officialRoster = await roster.CountAsync(r => r.IsOfficial, ct);
        var indicativeRoster = await roster.CountAsync(r => !r.IsOfficial, ct);

        var officialCast = await roster.CountAsync(
            r => r.IsOfficial && ballots.Any(b => b.RosterId == r.Id), ct);
        var indicativeCast = await roster.CountAsync(
            r => !r.IsOfficial && ballots.Any(b => b.RosterId == r.Id), ct);

        var changedBallots = await ballots.CountAsync(b => b.Revision > 1, ct);
        var totalRevisions = await ballots.SumAsync(b => b.Revision, ct);
        var lastBallotAt = await ballots
            .Select(b => (Instant?)b.UpdatedAt)
            .MaxAsync(ct);

        return new AssemblyVoteParticipation(
            officialRoster,
            indicativeRoster,
            officialCast,
            indicativeCast,
            changedBallots,
            totalRevisions,
            lastBallotAt);
    }

    // ==========================================================================
    // Peeks
    // ==========================================================================

    public async Task<bool> AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peek);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, peek.VoteId, ct);

        // Under the same lock as every other write to this vote: a peek row is published on
        // the results page as an early look at the tally, so it may only be written while
        // there is still something to look at early.
        var status = await ctx.AssemblyVotes.AsNoTracking()
            .Where(v => v.Id == peek.VoteId)
            .Select(v => (AssemblyVoteStatus?)v.Status)
            .FirstOrDefaultAsync(ct);
        if (status != AssemblyVoteStatus.Open) return false;

        ctx.AssemblyVotePeeks.Add(peek);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AssemblyVotePeek>> GetPeeksAsync(
        Guid voteId, CancellationToken ct = default) =>
        await WithContextAsync(async ctx => await ctx.AssemblyVotePeeks
            .AsNoTracking()
            .Where(p => p.VoteId == voteId)
            .OrderBy(p => p.PeekedAt) // arch:db-sort-ok chronological peek list
            .ToListAsync(ct), ct);

    // ==========================================================================
    // GDPR and account merge
    // ==========================================================================

    public async Task<IReadOnlyList<(AssemblyVoteRoster Roster, AssemblyVote Vote, AssemblyBallot? Ballot)>>
        GetVotingRecordForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var rosterRows = await ctx.AssemblyVoteRosterEntries
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .ToListAsync(ct);

        var voteIds = rosterRows.Select(r => r.VoteId).ToList();
        var rosterIds = rosterRows.Select(r => r.Id).ToList();

        var votes = await ctx.AssemblyVotes
            .AsNoTracking()
            .Where(v => voteIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var ballots = await ctx.AssemblyBallots
            .AsNoTracking()
            .Include(b => b.History)
            .Where(b => rosterIds.Contains(b.RosterId))
            .ToDictionaryAsync(b => b.RosterId, ct);

        return rosterRows
            .Select(r => (r, votes[r.VoteId], ballots.GetValueOrDefault(r.Id)))
            .ToList();
    }

    public async Task<(IReadOnlyList<AssemblyVote> Acted, IReadOnlyList<(AssemblyVotePeek Peek, AssemblyVote Vote)> Peeks)>
        GetActorRecordForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var acted = await ctx.AssemblyVotes
            .AsNoTracking()
            .Where(v => v.CreatedByUserId == userId
                        || v.OpenedByUserId == userId
                        || v.ClosedByUserId == userId)
            .ToListAsync(ct);

        var peeks = await ctx.AssemblyVotePeeks
            .AsNoTracking()
            .Where(p => p.AdminUserId == userId)
            .ToListAsync(ct);

        var peekVoteIds = peeks.Select(p => p.VoteId).ToList();
        var peekVotes = await ctx.AssemblyVotes
            .AsNoTracking()
            .Where(v => peekVoteIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        return (acted, peeks.Select(p => (p, peekVotes[p.VoteId])).ToList());
    }

    public async Task<int> AnonymizeRosterForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var rows = await ctx.AssemblyVoteRosterEntries
            .Where(r => r.UserId == userId)
            .ToListAsync(ct);

        foreach (var row in rows)
            row.UserId = null;

        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<AssemblyRosterDrop>> ReassignRosterToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.AssemblyVoteRosterEntries
            .Where(r => r.UserId == sourceUserId)
            .ToListAsync(ct);
        var voteIds = sourceRows.Select(r => r.VoteId).ToList();

        var targetVoteIds = await ctx.AssemblyVoteRosterEntries
            .Where(r => r.UserId == targetUserId && voteIds.Contains(r.VoteId))
            .Select(r => r.VoteId)
            .ToListAsync(ct);
        var targetVoteIdSet = targetVoteIds.ToHashSet();

        // Read the ballots before the cascade takes them: the audit entry has to name the
        // ballot it destroyed, and after SaveChanges the id is gone.
        var sourceRosterIds = sourceRows.Select(r => r.Id).ToList();
        var ballotByRosterId = await ctx.AssemblyBallots
            .Where(b => sourceRosterIds.Contains(b.RosterId))
            .ToDictionaryAsync(b => b.RosterId, b => b.Id, ct);

        var dropped = new List<AssemblyRosterDrop>();
        foreach (var row in sourceRows)
        {
            if (targetVoteIdSet.Contains(row.VoteId))
            {
                // The target already holds this vote's roster row; the source's row (and
                // its ballot/history, via cascade delete) is dropped rather than merged —
                // one person, one ballot per vote.
                dropped.Add(new AssemblyRosterDrop(
                    row.VoteId,
                    ballotByRosterId.TryGetValue(row.Id, out var ballotId) ? ballotId : null));
                ctx.AssemblyVoteRosterEntries.Remove(row);
            }
            else
            {
                row.UserId = targetUserId;
            }
        }

        // The merged account may also have drafted, opened, stopped or peeked at votes.
        // Those columns are this section's own and point at an account Users is about to
        // tombstone, so they move too — otherwise the acta loses the closer's name, the
        // published peek list credits "Merged User" instead of the surviving human, and the
        // GDPR export stops finding the votes this person drafted.
        var authored = await ctx.AssemblyVotes
            .Where(v => v.CreatedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var vote in authored)
        {
            vote.CreatedByUserId = targetUserId;
        }

        var closed = await ctx.AssemblyVotes
            .Where(v => v.ClosedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var vote in closed)
        {
            vote.ClosedByUserId = targetUserId;
        }

        var opened = await ctx.AssemblyVotes
            .Where(v => v.OpenedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var vote in opened)
        {
            vote.OpenedByUserId = targetUserId;
        }

        var peeks = await ctx.AssemblyVotePeeks
            .Where(p => p.AdminUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var peek in peeks)
        {
            peek.AdminUserId = targetUserId;
        }

        await ctx.SaveChangesAsync(ct);
        return dropped;
    }

    private async Task<T> WithContextAsync<T>(
        Func<GovernanceDbContext, Task<T>> action,
        CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await action(ctx);
    }
}

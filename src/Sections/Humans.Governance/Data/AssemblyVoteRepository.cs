using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodaTime;

namespace Humans.Governance.Data;

internal sealed class AssemblyVoteRepository(IDbContextFactory<GovernanceDbContext> factory)
    : IAssemblyVoteRepository
{
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
        AssemblyVote vote,
        AssemblyVoteStatus expectedStatus,
        Instant? expectedUpdatedAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, vote.Id, ct);

        var persisted = await ctx.AssemblyVotes.AsNoTracking()
            .Where(v => v.Id == vote.Id)
            .Select(v => new { v.Status, v.ClosesAt, v.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (persisted is null) return false;

        // A caller that spent time outside the transaction building this write — the
        // translator waiting on Google — says which revision of the row it read. The status
        // alone does not catch an edit that left the draft a draft, and this write carries
        // every authored field, so a newer edit would be overwritten.
        if (expectedUpdatedAt is { } since && persisted.UpdatedAt != since) return false;

        // Every lifecycle write is a snapshot read earlier, so a write built before somebody
        // else's transition would silently undo it. If the row has moved on — including to the
        // very state this write sets, as when two Stops race — nothing is written.
        if (persisted.Status != expectedStatus) return false;

        // The deadline of an open vote only ever moves later. Two concurrent Extends both
        // validate against the deadline they read, so the shorter could arrive second and pull
        // the deadline back; checked here, where the row is locked.
        if (persisted.Status == AssemblyVoteStatus.Open
            && vote.Status == AssemblyVoteStatus.Open
            && vote.ClosesAt < persisted.ClosesAt)
        {
            return false;
        }

        // An automatic close decided from the deadline it read. If the deadline has moved
        // later since, an Extend committed in between and is the newer fact: the vote is
        // still open, and this close is working from a deadline that no longer exists.
        if (vote.Status == AssemblyVoteStatus.Closed
            && vote.ClosedByUserId is null
            && persisted.ClosesAt > vote.ClosesAt)
        {
            return false;
        }

        // A Stop or Cancel built before an Extend committed carries the pre-extension deadline.
        // The extension is the newer fact and the deadline is part of the legal record, so the
        // locked row's deadline is kept rather than the terminal write being refused.
        if (persisted.Status == AssemblyVoteStatus.Open && vote.ClosesAt < persisted.ClosesAt)
        {
            vote.ClosesAt = persisted.ClosesAt;
        }

        ctx.AssemblyVotes.Update(vote);
        await ctx.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Opens the write transaction and holds the vote row's lock for it. Relational only: the
    /// in-memory test provider supports neither transactions nor raw SQL, so there the write
    /// proceeds on the state check alone.
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

    public async Task<bool> OpenWithRosterAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vote);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, vote.Id, ct);

        var persisted = await ctx.AssemblyVotes.FirstOrDefaultAsync(v => v.Id == vote.Id, ct);
        if (persisted is null || persisted.Status != AssemblyVoteStatus.Draft) return false;

        // The roster was built from the caller's snapshot of the draft. A Board edit committed
        // meanwhile would open the vote with new content and the old electorate, which a frozen
        // roster can never correct — so the open is refused and the caller rebuilds.
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

    public Task StampNotifiedAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default) =>
        StampAsync(rosterIds, announcedClosesAt, (row, stampedAt) => row.NotifiedAt = stampedAt, at, ct);

    public Task StampReminderSentAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default) =>
        StampAsync(rosterIds, announcedClosesAt, (row, stampedAt) => row.ReminderSentAt = stampedAt, at, ct);

    /// <summary>
    /// Writes a "this member was told" stamp only while the vote still closes at the deadline the
    /// message named: each stamp is an idempotency anchor, so one standing for a deadline the
    /// recipient was never told silently disenfranchises them. The deadline is re-read under the
    /// write, because an Extend between send and stamp clears only the stamps that existed then.
    /// </summary>
    private async Task StampAsync(
        IReadOnlyCollection<Guid> rosterIds,
        Instant announcedClosesAt,
        Action<AssemblyVoteRoster, Instant> stamp,
        Instant at,
        CancellationToken ct)
    {
        if (rosterIds.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.AssemblyVoteRosterEntries
            .Where(r => rosterIds.Contains(r.Id)
                && ctx.AssemblyVotes.Any(v => v.Id == r.VoteId && v.ClosesAt == announcedClosesAt))
            .ToListAsync(ct);

        foreach (var row in rows)
            stamp(row, at);

        await ctx.SaveChangesAsync(ct);
    }

    public async Task ClearReminderStampsAsync(
        Guid voteId, Instant stampedBy, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.AssemblyVoteRosterEntries
            .Where(r => r.VoteId == voteId
                && r.ReminderSentAt != null
                && r.ReminderSentAt <= stampedBy)
            .ToListAsync(ct);

        foreach (var row in rows)
            row.ReminderSentAt = null;

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

    /// <summary>
    /// Writes a member's ballot and its history row. Returns null when the vote is no longer
    /// accepting ballots.
    /// </summary>
    /// <remarks>
    /// The vote's state is re-read under the row's lock: a Stop or automatic close can land
    /// mid-request, and closure persists Closed before reading ballots, so an accepted ballot is
    /// always one the count saw.
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

        // Hold the vote row's lock until the ballot commits; closure's status UPDATE waits on it.
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

    public async Task<bool> AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(peek);

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await BeginLockedWriteAsync(ctx, peek.VoteId, ct);

        // A peek is published as an early look, so it is written only while there is still
        // something to look at early: the deadline is compared against the peek's own instant,
        // the same AcceptsBallotsAt test a ballot write takes under this lock.
        var vote = await ctx.AssemblyVotes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == peek.VoteId, ct);
        if (vote is null || !vote.AcceptsBallotsAt(peek.PeekedAt)) return false;

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

    public async Task<IReadOnlyList<Guid>> ReassignVoteActorsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Roster rows and ballots are deliberately NOT re-pointed: they are frozen evidence of
        // who was entitled and what they cast. Two accounts on one roster is a defect in the
        // vote that the merge must not hide; MergedUserIds still reaches these rows for erasure.
        var rostered = await ctx.AssemblyVoteRosterEntries
            .Where(r => r.UserId == sourceUserId)
            .Select(r => r.VoteId)
            .ToListAsync(ct);

        // Actor columns move, or the acta and peek list name a tombstone. A new actor column
        // joins both the predicate and the loop; a test
        // (ReassignAsync_MovesEachActorColumn_WhenItIsTheOnlyOneOnTheVote) catches them drifting.
        var acted = await ctx.AssemblyVotes
            .Where(v => v.CreatedByUserId == sourceUserId
                || v.ClosedByUserId == sourceUserId
                || v.OpenedByUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var vote in acted)
        {
            if (vote.CreatedByUserId == sourceUserId) vote.CreatedByUserId = targetUserId;
            if (vote.ClosedByUserId == sourceUserId) vote.ClosedByUserId = targetUserId;
            if (vote.OpenedByUserId == sourceUserId) vote.OpenedByUserId = targetUserId;
        }

        var peeks = await ctx.AssemblyVotePeeks
            .Where(p => p.AdminUserId == sourceUserId)
            .ToListAsync(ct);
        foreach (var peek in peeks)
        {
            peek.AdminUserId = targetUserId;
        }

        await ctx.SaveChangesAsync(ct);
        return rostered;
    }

    private async Task<T> WithContextAsync<T>(
        Func<GovernanceDbContext, Task<T>> action,
        CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await action(ctx);
    }
}

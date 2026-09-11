using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
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

    public async Task UpdateAsync(AssemblyVote vote, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AssemblyVotes.Update(vote);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReplaceOptionsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteOption> options, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

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
    }

    public async Task DeleteAsync(Guid voteId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var vote = await ctx.AssemblyVotes.FindAsync([voteId], ct);
        if (vote is null)
            return;

        ctx.AssemblyVotes.Remove(vote);
        await ctx.SaveChangesAsync(ct);
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

    public async Task OpenWithRosterAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AssemblyVotes.Update(vote);
        ctx.AssemblyVoteRosterEntries.AddRange(roster);
        await ctx.SaveChangesAsync(ct);
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
    /// The vote's state is re-read here, inside the same unit of work as the write, and not
    /// taken from whatever the caller saw at the top of the request: a Stop or an automatic
    /// close can land in between, and a ballot accepted after the tally was taken would be
    /// told "recorded" while never appearing in the stored result. Closure persists the closed
    /// status before it reads any ballots, so this check and that ordering together mean an
    /// accepted ballot is always one the count saw.
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

    public async Task AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.AssemblyVotePeeks.Add(peek);
        await ctx.SaveChangesAsync(ct);
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

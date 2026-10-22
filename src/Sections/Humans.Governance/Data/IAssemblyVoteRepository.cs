using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Data;

/// <summary>
/// <see cref="GetParticipationAsync"/> returns counts and timestamps only, so it is safe to
/// serve during the embargo. Ballot content is reachable only through
/// <see cref="GetBallotsAsync"/> and <see cref="GetBallotForRosterAsync"/>, and the service
/// gates both. History is append-only: <see cref="UpsertBallotAsync"/> is its only writer.
/// </summary>
internal interface IAssemblyVoteRepository : IRepository
{
    /// <summary>Loads a vote with its authored options. Null when it does not exist.</summary>
    Task<AssemblyVote?> GetByIdAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Every vote with its options, newest first. The dataset is a handful of rows a year,
    /// so the list page filters and orders in memory rather than in SQL.
    /// </summary>
    Task<IReadOnlyList<AssemblyVote>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(AssemblyVote vote, CancellationToken ct = default);

    /// <summary>
    /// Writes a vote snapshot under the row's lock only while the persisted row is still in
    /// <paramref name="expectedStatus"/> (and, when given, at <paramref name="expectedUpdatedAt"/>);
    /// returns false otherwise. An automatic close is also refused once an Extend has moved the
    /// deadline it read.
    /// </summary>
    Task<bool> UpdateAsync(
        AssemblyVote vote,
        AssemblyVoteStatus expectedStatus,
        Instant? expectedUpdatedAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces a draft's authored options wholesale, then persists the vote, under the row's
    /// lock. Draft-only; options are immutable once the vote opens, so an edit that arrives
    /// after the vote has opened returns false and writes nothing.
    /// </summary>
    Task<bool> ReplaceOptionsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteOption> options, CancellationToken ct = default);

    /// <summary>
    /// Deletes a draft and its options, under the row's lock. Returns false without deleting
    /// when the persisted row is no longer a draft — an opened vote has an electorate and a
    /// frozen roster, and is never deleted.
    /// </summary>
    Task<bool> DeleteAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Every Open vote whose <c>ClosesAt</c> is at or before <paramref name="now"/> — the
    /// lapse sweep's work list.
    /// </summary>
    Task<IReadOnlyList<AssemblyVote>> GetLapsedOpenVotesAsync(Instant now, CancellationToken ct = default);

    /// <summary>
    /// Every Open vote whose <c>ClosesAt</c> falls within the reminder window
    /// (<paramref name="from"/>..<paramref name="to"/>), for the T-24h reminder sweep.
    /// </summary>
    Task<IReadOnlyList<AssemblyVote>> GetOpenVotesClosingBetweenAsync(
        Instant from, Instant to, CancellationToken ct = default);

    /// <summary>
    /// Opens the vote and writes its roster in one unit of work, so a vote can never be
    /// Open without the roster that defines its electorate. Under the row's lock: returns
    /// false without writing when the persisted row is no longer a draft, or when it has been
    /// edited since the caller read it — the roster is built from the draft and frozen, so a
    /// vote must never open with new content and an electorate computed from the old.
    /// </summary>
    Task<bool> OpenWithRosterAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct = default);

    Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// The roster row entitling <paramref name="userId"/> to vote, or null when they are
    /// not on this vote's roster.
    /// </summary>
    Task<AssemblyVoteRoster?> GetRosterRowAsync(
        Guid voteId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>NotifiedAt</c> only while the vote still closes at
    /// <paramref name="announcedClosesAt"/>; an extension mid-batch leaves the row for the retry
    /// sweep to send a corrected notice.
    /// </summary>
    Task StampNotifiedAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>ReminderSentAt</c> only while the vote still closes at
    /// <paramref name="announcedClosesAt"/>: the stamp is the reminder's idempotency anchor, so it
    /// must never stand for a deadline the recipient was not told.
    /// </summary>
    Task StampReminderSentAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default);

    /// <summary>
    /// Re-arms the T-24h reminder after an extension by clearing stamps at or before
    /// <paramref name="stampedBy"/>. Later stamps already announced the new deadline.
    /// </summary>
    Task ClearReminderStampsAsync(Guid voteId, Instant stampedBy, CancellationToken ct = default);

    /// <summary>
    /// Roster rows for a vote that have no ballot and no reminder yet — who the T-24h
    /// reminder goes to.
    /// </summary>
    Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterNeedingReminderAsync(
        Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Inserts at revision 1 or bumps the revision, appending a history row in the same unit of
    /// work. Null when the vote no longer accepts ballots at <paramref name="now"/>, re-read under
    /// the vote row's lock so a close landing mid-request cannot leave an accepted ballot uncounted.
    /// </summary>
    Task<AssemblyBallot?> UpsertBallotAsync(
        Guid voteId,
        Guid rosterId,
        AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// The standing ballot for a roster row, with its revision history. Ballot content —
    /// the service gates every caller.
    /// </summary>
    Task<AssemblyBallot?> GetBallotForRosterAsync(
        Guid voteId, Guid rosterId, CancellationToken ct = default);

    /// <summary>
    /// Every standing ballot for a vote. Ballot content — the service permits this only at
    /// close, behind the audited peek, or for an audited Board/Admin read.
    /// </summary>
    Task<IReadOnlyList<AssemblyBallot>> GetBallotsAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Participation counts only — nothing derived from ballot content, which is what makes it
    /// safe to show during the embargo.
    /// </summary>
    Task<AssemblyVoteParticipation> GetParticipationAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Records that an Admin looked at the live tally. Called in the same unit of work as
    /// the read it permits, under the vote row's lock: returns false without writing when the
    /// vote is no longer Open, because the peek log records looking early and a closed vote
    /// has nothing to look at early.
    /// </summary>
    Task<bool> AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default);

    /// <summary>Every peek on a vote, oldest first. Shown on the results page.</summary>
    Task<IReadOnlyList<AssemblyVotePeek>> GetPeeksAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Every roster row for a user, across votes, with the standing ballot and its history
    /// where one exists — the member's own voting record, for the GDPR export.
    /// </summary>
    Task<IReadOnlyList<(AssemblyVoteRoster Roster, AssemblyVote Vote, AssemblyBallot? Ballot)>>
        GetVotingRecordForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The votes a user acted on as an author or officer (drafted, opened, closed) and every
    /// tally they peeked at — the other half of their personal data here, which the roster
    /// query misses entirely for a Board member who runs a vote without being on its roster.
    /// </summary>
    Task<(IReadOnlyList<AssemblyVote> Acted, IReadOnlyList<(AssemblyVotePeek Peek, AssemblyVote Vote)> Peeks)>
        GetActorRecordForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Art. 17 erasure: nulls <c>UserId</c> on every roster row for the user, leaving the
    /// rows as anonymous tombstones so turnout counts and stored results stay valid.
    /// Ballots and history are retained unlinked — the vote is the association's legal
    /// record (GDPR Art. 17(3)(b) and (e)). Returns the number of rows anonymized.
    /// </summary>
    Task<int> AnonymizeRosterForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Account merge: moves the section's actor references off the source account so the acta
    /// and peek list keep naming the surviving human. Roster rows and ballots stay put — they are
    /// evidence, not account state. Returns the votes the source was rostered on, for the audit.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignVoteActorsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
}

/// <summary>
/// Participation counts for one vote. Deliberately holds no tally: this is the shape the
/// embargo permits, so it must stay free of anything derived from ballot content.
/// </summary>
internal sealed record AssemblyVoteParticipation(
    int OfficialRoster,
    int IndicativeRoster,
    int OfficialCast,
    int IndicativeCast,
    int ChangedBallots,
    int TotalRevisions,
    Instant? LastBallotAt);

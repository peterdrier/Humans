using Humans.Base.Interfaces.Repositories;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Data;

/// <summary>
/// Repository for the assembly-vote aggregate (<c>assembly_votes</c>,
/// <c>assembly_vote_options</c>, <c>assembly_vote_roster</c>, <c>assembly_ballots</c>,
/// <c>assembly_ballot_history</c>, <c>assembly_vote_peeks</c>). The only non-test file that
/// may touch those DbSets.
/// </summary>
/// <remarks>
/// Two shapes here exist because of the embargo, not for convenience.
/// <see cref="GetParticipationAsync"/> returns counts and timestamps only — never anything
/// derived from ballot content — so it is safe to serve to every member while a vote is
/// open. Ballot content is reachable only through <see cref="GetBallotsAsync"/> and
/// <see cref="GetBallotForRosterAsync"/>, and the service gates both.
/// <para>
/// History is append-only (design-rules §12): the only writer is
/// <see cref="UpsertBallotAsync"/>, which appends a revision in the same unit of work as
/// the ballot it records. There is no update and no delete.
/// </para>
/// </remarks>
internal interface IAssemblyVoteRepository : IRepository
{
    // ==========================================================================
    // Votes
    // ==========================================================================

    /// <summary>Loads a vote with its authored options. Null when it does not exist.</summary>
    Task<AssemblyVote?> GetByIdAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// Every vote with its options, newest first. The dataset is a handful of rows a year,
    /// so the list page filters and orders in memory rather than in SQL.
    /// </summary>
    Task<IReadOnlyList<AssemblyVote>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Persists a new draft, including its authored options.</summary>
    Task AddAsync(AssemblyVote vote, CancellationToken ct = default);

    /// <summary>
    /// Writes a vote snapshot back, under the row's lock, only while the persisted row is
    /// still in <paramref name="expectedStatus"/> — the state the caller read before building
    /// this write. Returns false without writing when the row has moved on, so a snapshot
    /// taken before somebody else's transition cannot undo it, and two requests that both read
    /// Open cannot both close the vote. An automatic close is refused as well when an Extend
    /// has pushed the deadline past the one it read.
    /// <para>
    /// <paramref name="expectedUpdatedAt"/> narrows that further to the exact revision the
    /// caller read: for a write built while an external call was in flight, status alone does
    /// not catch an edit that left a draft a draft.
    /// </para>
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

    // ==========================================================================
    // Roster — written once at open, never recomputed
    // ==========================================================================

    /// <summary>
    /// Opens the vote and writes its roster in one unit of work, so a vote can never be
    /// Open without the roster that defines its electorate. Under the row's lock: returns
    /// false without writing when the persisted row is no longer a draft, or when it has been
    /// edited since the caller read it — the roster is built from the draft and frozen, so a
    /// vote must never open with new content and an electorate computed from the old.
    /// </summary>
    Task<bool> OpenWithRosterAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteRoster> roster, CancellationToken ct = default);

    /// <summary>The whole roster for a vote.</summary>
    Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>
    /// The roster row entitling <paramref name="userId"/> to vote, or null when they are
    /// not on this vote's roster.
    /// </summary>
    Task<AssemblyVoteRoster?> GetRosterRowAsync(
        Guid voteId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>NotifiedAt</c> on the given roster rows, but only while the vote still closes
    /// at <paramref name="announcedClosesAt"/> — the deadline the opening email just sent
    /// actually names. Called after those emails are queued. The stamp is what excludes a row
    /// from the retry sweep, so an extension landing mid-batch skips it and the sweep sends a
    /// corrected opening notice rather than leaving that member holding the old deadline.
    /// </summary>
    Task StampNotifiedAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>ReminderSentAt</c> on the given roster rows, but only while the vote still
    /// closes at <paramref name="announcedClosesAt"/> — the deadline the email that was just
    /// sent actually names. The idempotency anchor for the T-24h reminder: a stamped row is
    /// never reminded again, so a stamp must never stand for a deadline the recipient was
    /// not told. An extension landing mid-send moves the deadline, the stamp is skipped, and
    /// the next sweep reminds that member of the deadline now in force.
    /// </summary>
    Task StampReminderSentAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, Instant announcedClosesAt,
        CancellationToken ct = default);

    /// <summary>
    /// Clears <c>ReminderSentAt</c> on the roster rows of a vote stamped at or before
    /// <paramref name="stampedBy"/>, so the T-24h reminder is armed again. Called when an
    /// extension moves the deadline: the stamp means "already told about the old deadline",
    /// and the new one has to be announced too. Later stamps are left alone — a sweep still
    /// running when the extension commits reads the new deadline per recipient and announces
    /// that one, and re-arming it would mail the same member twice.
    /// </summary>
    Task ClearReminderStampsAsync(Guid voteId, Instant stampedBy, CancellationToken ct = default);

    /// <summary>
    /// Roster rows for a vote that have no ballot and no reminder yet — who the T-24h
    /// reminder goes to.
    /// </summary>
    Task<IReadOnlyList<AssemblyVoteRoster>> GetRosterNeedingReminderAsync(
        Guid voteId, CancellationToken ct = default);

    // ==========================================================================
    // Ballots
    // ==========================================================================

    /// <summary>
    /// Casts or changes a ballot: inserts at revision 1, or bumps the revision and
    /// overwrites the standing choice. Either way a history row is appended in the same
    /// unit of work. Returns the resulting ballot, or null when the vote was no longer
    /// accepting ballots at <paramref name="now"/> — re-read here under the vote row's lock
    /// rather than trusted from the caller, so a close landing mid-request cannot leave an
    /// accepted ballot uncounted.
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
    /// Participation counts for a vote: roster sizes, ballots cast, how many ballots were
    /// changed at least once, total revisions, and when the last ballot landed. Contains
    /// nothing derived from ballot content, which is what makes it safe to show during the
    /// embargo.
    /// </summary>
    Task<AssemblyVoteParticipation> GetParticipationAsync(Guid voteId, CancellationToken ct = default);

    // ==========================================================================
    // Peeks
    // ==========================================================================

    /// <summary>
    /// Records that an Admin looked at the live tally. Called in the same unit of work as
    /// the read it permits, under the vote row's lock: returns false without writing when the
    /// vote is no longer Open, because the peek log records looking early and a closed vote
    /// has nothing to look at early.
    /// </summary>
    Task<bool> AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default);

    /// <summary>Every peek on a vote, oldest first. Shown on the results page.</summary>
    Task<IReadOnlyList<AssemblyVotePeek>> GetPeeksAsync(Guid voteId, CancellationToken ct = default);

    // ==========================================================================
    // GDPR and account merge
    // ==========================================================================

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
    /// Account merge: moves the section's own actor references — <c>CreatedByUserId</c>,
    /// <c>OpenedByUserId</c>, <c>ClosedByUserId</c> and a peek's <c>AdminUserId</c> — off
    /// the source account, so the acta and the published peek list keep naming the
    /// surviving human rather than the tombstone Users leaves behind.
    /// <para>
    /// Roster rows and ballots are deliberately left on the source account. A roster is the
    /// record of who was entitled to vote when the vote opened and a ballot is what that
    /// entitlement produced: both are evidence, not account state. Two accounts on one
    /// roster means one human was enrolled twice, and two ballots means they voted twice —
    /// a defect in the vote that the merge must not tidy away. The merge chain
    /// (<c>UserInfo.MergedUserIds</c>) still resolves the survivor to every id folded
    /// into it, which is how GDPR erasure already reaches these rows.
    /// </para>
    /// Returns the votes the source account was rostered on, so the caller can audit that
    /// the merge saw those rows and left them alone.
    /// </summary>
    Task<IReadOnlyList<Guid>> ReassignVoteActorsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
}

/// <summary>
/// Participation counts for one vote. Deliberately holds no tally: this is the shape the
/// embargo permits, so it must stay free of anything derived from ballot content.
/// </summary>
/// <param name="OfficialRoster">Roster rows entitled to an official ballot.</param>
/// <param name="IndicativeRoster">Roster rows entitled to an indicative ballot.</param>
/// <param name="OfficialCast">Official ballots cast.</param>
/// <param name="IndicativeCast">Indicative ballots cast.</param>
/// <param name="ChangedBallots">Ballots revised at least once.</param>
/// <param name="TotalRevisions">Sum of every ballot's revision count.</param>
/// <param name="LastBallotAt">When the most recent ballot was cast or changed.</param>
internal sealed record AssemblyVoteParticipation(
    int OfficialRoster,
    int IndicativeRoster,
    int OfficialCast,
    int IndicativeCast,
    int ChangedBallots,
    int TotalRevisions,
    Instant? LastBallotAt);

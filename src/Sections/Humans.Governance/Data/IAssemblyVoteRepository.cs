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

    /// <summary>Persists changes to an existing vote.</summary>
    Task UpdateAsync(AssemblyVote vote, CancellationToken ct = default);

    /// <summary>
    /// Replaces a draft's authored options wholesale, then persists the vote. Draft-only;
    /// options are immutable once the vote opens.
    /// </summary>
    Task ReplaceOptionsAsync(
        AssemblyVote vote, IReadOnlyList<AssemblyVoteOption> options, CancellationToken ct = default);

    /// <summary>Deletes a draft and its options. Never called for a vote that has opened.</summary>
    Task DeleteAsync(Guid voteId, CancellationToken ct = default);

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
    /// Open without the roster that defines its electorate.
    /// </summary>
    Task OpenWithRosterAsync(
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
    /// Stamps <c>NotifiedAt</c> on the given roster rows. Called after the open emails are
    /// queued.
    /// </summary>
    Task StampNotifiedAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>ReminderSentAt</c> on the given roster rows. The idempotency anchor for
    /// the T-24h reminder: a stamped row is never reminded again.
    /// </summary>
    Task StampReminderSentAsync(
        IReadOnlyCollection<Guid> rosterIds, Instant at, CancellationToken ct = default);

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
    /// unit of work. Returns the resulting ballot.
    /// </summary>
    Task<AssemblyBallot> UpsertBallotAsync(
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
    /// the read it permits.
    /// </summary>
    Task AddPeekAsync(AssemblyVotePeek peek, CancellationToken ct = default);

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
    /// Art. 17 erasure: nulls <c>UserId</c> on every roster row for the user, leaving the
    /// rows as anonymous tombstones so turnout counts and stored results stay valid.
    /// Ballots and history are retained unlinked — the vote is the association's legal
    /// record (GDPR Art. 17(3)(b) and (e)). Returns the number of rows anonymized.
    /// </summary>
    Task<int> AnonymizeRosterForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Account merge: moves the source account's roster rows to the target. Where both
    /// accounts sit on the same vote's roster the target's row wins and the source's row
    /// and ballot are dropped — one person may hold only one ballot per vote. Returns what
    /// each drop destroyed, so the caller can audit the real entities rather than the
    /// roster row's own id.
    /// <para>
    /// Also moves the section's own actor references — <c>OpenedByUserId</c>,
    /// <c>ClosedByUserId</c> and a peek's <c>AdminUserId</c> — off the source account, so
    /// the acta and the published peek list keep naming the surviving human rather than the
    /// tombstone Users leaves behind.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AssemblyRosterDrop>> ReassignRosterToUserAsync(
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

/// <summary>
/// One roster row dropped by an account merge: the vote it sat on, and the ballot it
/// destroyed when it carried one. <c>BallotId</c> is null when the merged-from account was
/// on the roster but never voted.
/// </summary>
internal sealed record AssemblyRosterDrop(Guid VoteId, Guid? BallotId);

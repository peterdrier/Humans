using Humans.Base.Interfaces;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// The business rules for assembly votes: drafting, opening with a frozen roster, accepting
/// and amending ballots, closing, counting, and the embargo that keeps the tally invisible
/// until close.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The embargo is enforced here, not in the controllers.</strong> Only three members
/// of this interface can return anything derived from ballot content:
/// <see cref="GetResultsAsync"/> (which serves the stored result and refuses while the vote
/// is open), <see cref="PeekAsync"/> (which writes its audit entry and peek row in the same
/// unit of work as the read), and <see cref="GetBallotsForBoardAsync"/> (post-close and
/// audited). Everything else returns counts and timestamps only. Adding a fourth content
/// path is a change to the association's voting guarantees, not a refactor.
/// </para>
/// <para>
/// Every read and write first settles the vote's state: a vote whose <c>ClosesAt</c> has
/// passed is closed — result computed, stored and audited — before the caller is served, so
/// the hourly sweep is a backstop rather than the thing that makes closing correct.
/// </para>
/// <para>
/// Nothing here is on <c>Humans.Governance.Contracts</c>. No other section reads votes, and
/// the roster is derived from Governance's own data.
/// </para>
/// </remarks>
internal interface IAssemblyVoteService : IApplicationService
{
    // ==========================================================================
    // Member-facing
    // ==========================================================================

    /// <summary>
    /// Every vote, open first, as seen by <paramref name="userId"/>. Every logged-in member
    /// sees every vote; roster membership only changes whether they can cast.
    /// </summary>
    Task<IReadOnlyList<AssemblyVoteListItem>> GetVotesForMemberAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// One vote as seen by <paramref name="userId"/>: text, link, participation counts, and
    /// — for roster members — their own ballot and its history. Never a tally.
    /// </summary>
    Task<AssemblyVoteDetail?> GetVoteForMemberAsync(
        Guid voteId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Casts or changes <paramref name="userId"/>'s ballot. Rejects a submitter who is not
    /// on the roster, a vote that is not open, and a malformed ballot; on success appends a
    /// history row and writes an audit entry that names the voter but not the choice.
    /// </summary>
    Task<BallotSubmissionOutcome> CastBallotAsync(
        Guid voteId,
        Guid userId,
        Domain.AssemblyBallotChoice choice,
        IReadOnlyList<string>? ranking,
        CancellationToken ct = default);

    /// <summary>
    /// The results of a closed vote: the stored result, the peek list, the acta block, and
    /// individual ballots where disclosure allows. Returns null while the vote is open —
    /// this is the embargo, and it applies to Board and Admin too.
    /// </summary>
    /// <param name="viewerIsBoardOrAdmin">
    /// Grants the individual-ballot list regardless of the vote's disclosure switch, and
    /// causes the view to be audited.
    /// </param>
    Task<AssemblyVoteResultsView?> GetResultsAsync(
        Guid voteId, Guid userId, bool viewerIsBoardOrAdmin, CancellationToken ct = default);

    // ==========================================================================
    // Drafting — BoardOrAdmin
    // ==========================================================================

    /// <summary>Every vote in every state, for the admin list.</summary>
    Task<IReadOnlyList<AssemblyVoteListItem>> GetAllForAdminAsync(CancellationToken ct = default);

    /// <summary>The authored content of a draft, for the edit form.</summary>
    Task<AssemblyVoteDraft?> GetDraftAsync(Guid voteId, CancellationToken ct = default);

    /// <summary>Creates a draft. Returns null when the draft is rejected as invalid.</summary>
    Task<Guid?> CreateDraftAsync(
        AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Replaces a draft's authored content. Draft-only: content is immutable once the vote
    /// is Open, where the sole permitted change is extending <c>ClosesAt</c>.
    /// </summary>
    Task<AssemblyVoteActionResult> UpdateDraftAsync(
        Guid voteId, AssemblyVoteDraft draft, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Machine-translates the draft's authored text — title, official text and every option
    /// label — from its <c>OfficialCulture</c> into the target cultures, filling only what is
    /// blank. Authored text is never overwritten, so this is an authoring assist and not a
    /// source of truth: the official-culture text stays the binding version.
    /// <para>
    /// Draft-only. A vote's content is immutable once Open, and a machine translation of a
    /// motion the electorate is already voting on would change what some members are reading
    /// mid-vote.
    /// </para>
    /// </summary>
    /// <returns>How many blanks were filled; 0 when there was nothing to fill.</returns>
    Task<int> PreFillTranslationsAsync(
        Guid voteId, IReadOnlyList<string> targetCultures, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Deletes a draft. Never permitted once the vote has opened.</summary>
    Task<AssemblyVoteActionResult> DeleteDraftAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default);

    // ==========================================================================
    // Lifecycle — AdminOnly
    // ==========================================================================

    /// <summary>
    /// Opens a draft: snapshots the roster (active Asociados and Board role holders as
    /// official, the configured indicative audience alongside), emails everyone on it,
    /// raises the in-app notification, and audits with the roster counts.
    /// </summary>
    Task<AssemblyVoteActionResult> OpenAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default);

    /// <summary>Closes an open vote now, computing and storing the result. Terminal.</summary>
    Task<AssemblyVoteActionResult> StopAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default);

    /// <summary>
    /// Moves <c>ClosesAt</c> later on an open vote. Shortening is not offered — that is
    /// what Stop is for — so an earlier time is rejected.
    /// </summary>
    Task<AssemblyVoteActionResult> ExtendAsync(
        Guid voteId, Instant newClosesAt, Guid adminUserId, CancellationToken ct = default);

    /// <summary>
    /// Cancels an open vote with a required reason: ballots are retained for the record, no
    /// result is computed, and the roster is emailed. Terminal. This is the exit for an
    /// assembly that refuses electronic voting (statutes Art. 8.2).
    /// </summary>
    Task<AssemblyVoteActionResult> CancelAsync(
        Guid voteId, string reason, Guid adminUserId, CancellationToken ct = default);

    /// <summary>
    /// The live tally of an open vote, for an Admin who has to make a call at the assembly.
    /// Writes the audit entry and the peek row in the same unit of work as the read, and the
    /// peek is listed on the results page afterwards. Returns null when there is no such vote.
    /// </summary>
    Task<AssemblyVoteResult?> PeekAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default);

    /// <summary>
    /// Every individual ballot on a closed vote, for Board and Admin. Audited on every
    /// call. Returns null while the vote is open.
    /// </summary>
    Task<IReadOnlyList<AssemblyBallotDisclosureRow>?> GetBallotsForBoardAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default);

    // ==========================================================================
    // Automation
    // ==========================================================================

    /// <summary>
    /// The hourly sweep: closes votes whose deadline has passed and sends the T-24h
    /// reminder to roster members who have not voted. Returns how many votes it closed.
    /// </summary>
    Task<int> RunLapseAndReminderSweepAsync(CancellationToken ct = default);
}

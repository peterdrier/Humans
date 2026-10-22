using Humans.Base.Interfaces;
using Humans.Governance.Services.Dtos;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// <strong>The embargo is enforced here, not in the controllers.</strong> Only
/// <see cref="GetResultsAsync"/>, <see cref="PeekAsync"/> and <see cref="GetBallotsForBoardAsync"/>
/// return anything derived from ballot content; a fourth is a change to the association's voting
/// guarantees, not a refactor.
/// </summary>
/// <remarks>
/// Every read and write first settles the vote: one past <c>ClosesAt</c> is closed before the
/// caller is served, so the hourly sweep is a backstop.
/// </remarks>
internal interface IAssemblyVoteService : IApplicationService
{
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
    /// The results of a closed vote. Null while the vote is open — the embargo applies to Board
    /// and Admin too.
    /// </summary>
    /// <remarks>
    /// <paramref name="viewerIsBoardOrAdmin"/> grants the individual-ballot list regardless of the
    /// disclosure switch, and the view is audited.
    /// </remarks>
    Task<AssemblyVoteResultsView?> GetResultsAsync(
        Guid voteId, Guid userId, bool viewerIsBoardOrAdmin, CancellationToken ct = default);

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
    /// Machine-translates the draft's blank cultures from its <c>OfficialCulture</c>; authored text
    /// is never overwritten. Draft-only: translating a motion mid-vote would change what members read.
    /// </summary>
    /// <returns>How many blanks were filled; 0 when there was nothing to fill.</returns>
    Task<int> PreFillTranslationsAsync(
        Guid voteId, IReadOnlyList<string> targetCultures, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>Deletes a draft. Never permitted once the vote has opened.</summary>
    Task<AssemblyVoteActionResult> DeleteDraftAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default);

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
    /// The live tally of an open vote, with the audit entry and peek row written in the same unit
    /// of work. <c>Recorded</c> is false when the vote had already closed, so the caller can send
    /// the reader to the results page instead.
    /// </summary>
    Task<(AssemblyVoteResult? Result, bool Recorded)> PeekAsync(
        Guid voteId, Guid adminUserId, CancellationToken ct = default);

    /// <summary>
    /// Every individual ballot on a closed vote, for Board and Admin. Audited on every
    /// call. Returns null while the vote is open.
    /// </summary>
    Task<IReadOnlyList<AssemblyBallotDisclosureRow>?> GetBallotsForBoardAsync(
        Guid voteId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// The hourly sweep: closes votes whose deadline has passed and sends the T-24h
    /// reminder to roster members who have not voted. Returns how many votes it closed.
    /// </summary>
    Task<int> RunLapseAndReminderSweepAsync(CancellationToken ct = default);
}

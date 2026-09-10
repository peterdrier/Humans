using Humans.Governance.Data;
using Humans.Governance.Domain;
using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// One vote as it appears in a member's list. Carries participation counts but never a
/// tally, so the same shape serves an open vote and a closed one.
/// </summary>
internal sealed record AssemblyVoteListItem(
    Guid Id,
    string Title,
    AssemblyVoteStatus Status,
    AssemblyVoteKind Kind,
    Instant ClosesAt,
    Instant? ClosedAt,
    bool IsOnRoster,
    bool IsOfficial,
    bool HasBallot,
    AssemblyVoteParticipation Participation);

/// <summary>
/// A vote as shown on its own page. Every logged-in member gets one of these; only the
/// ballot-form fields differ by whether the viewer is on the roster.
/// </summary>
/// <param name="IsTranslation">
/// True when the text shown is a translation rather than the binding official-culture text,
/// so the page can say so.
/// </param>
/// <param name="CanCastBallot">
/// True only when the viewer is on the roster and the vote still accepts ballots. The
/// controller shows the form on this alone.
/// </param>
/// <param name="OwnBallot">The viewer's own standing ballot and its history; null if they have not voted.</param>
internal sealed record AssemblyVoteDetail(
    Guid Id,
    string Title,
    string OfficialText,
    string OfficialCulture,
    bool IsTranslation,
    string? InfoUrl,
    AssemblyVoteKind Kind,
    RequiredMajority RequiredMajority,
    IndicativeAudience IndicativeAudience,
    BallotDisclosure BallotDisclosure,
    AssemblyVoteStatus Status,
    Instant ClosesAt,
    Instant? ClosedAt,
    LocalDate? AssemblyDate,
    string? CancelReason,
    bool IsOnRoster,
    bool IsOfficial,
    bool CanCastBallot,
    IReadOnlyList<AssemblyVoteOptionView> Options,
    AssemblyBallotView? OwnBallot,
    AssemblyVoteParticipation Participation);

/// <summary>One authored option, resolved to the viewer's culture.</summary>
internal sealed record AssemblyVoteOptionView(string Key, string Label, int Order);

/// <summary>A member's own ballot, with the full revision history they are entitled to see.</summary>
internal sealed record AssemblyBallotView(
    AssemblyBallotChoice Choice,
    IReadOnlyList<string>? Ranking,
    int Revision,
    Instant CastAt,
    Instant UpdatedAt,
    IReadOnlyList<AssemblyBallotRevisionView> History);

/// <summary>One recorded revision of a ballot.</summary>
internal sealed record AssemblyBallotRevisionView(
    int Revision,
    AssemblyBallotChoice Choice,
    IReadOnlyList<string>? Ranking,
    Instant RecordedAt);

/// <summary>
/// The results page after close: the stored result, who peeked early, the acta block the
/// Secretary pastes into the minutes, and — only where disclosure allows — the individual
/// ballots.
/// </summary>
internal sealed record AssemblyVoteResultsView(
    AssemblyVoteDetail Vote,
    AssemblyVoteResult Result,
    IReadOnlyList<AssemblyVotePeekView> Peeks,
    IReadOnlyList<AssemblyBallotDisclosureRow>? DisclosedBallots,
    string ActaText);

/// <summary>An Admin who looked at the tally before close. Always visible after close.</summary>
internal sealed record AssemblyVotePeekView(Guid AdminUserId, string AdminName, Instant PeekedAt);

/// <summary>
/// One member's ballot as disclosed after close. Reachable only by Board/Admin (audited) or,
/// when the vote's disclosure switch is on, by roster members.
/// </summary>
/// <param name="UserId">Null when the roster row has been anonymized by erasure.</param>
internal sealed record AssemblyBallotDisclosureRow(
    Guid? UserId,
    string DisplayName,
    bool IsOfficial,
    AssemblyBallotChoice Choice,
    IReadOnlyList<string>? Ranking,
    int Revision);

/// <summary>Authored content for a draft vote, as submitted by the Board.</summary>
internal sealed record AssemblyVoteDraft(
    IReadOnlyDictionary<string, string> Title,
    IReadOnlyDictionary<string, string> OfficialText,
    string OfficialCulture,
    string? InfoUrl,
    AssemblyVoteKind Kind,
    RequiredMajority RequiredMajority,
    IndicativeAudience IndicativeAudience,
    BallotDisclosure BallotDisclosure,
    LocalDate? AssemblyDate,
    Instant ClosesAt,
    IReadOnlyList<AssemblyVoteDraftOption> Options);

/// <summary>One authored option on a draft.</summary>
internal sealed record AssemblyVoteDraftOption(
    string Key,
    int Order,
    IReadOnlyDictionary<string, string> Label);

/// <summary>
/// Why a lifecycle action did or did not happen. The controller maps these to status codes;
/// the service never throws for an ordinary refusal.
/// </summary>
internal enum AssemblyVoteActionResult
{
    /// <summary>The action was applied.</summary>
    Ok = 0,

    /// <summary>No such vote.</summary>
    NotFound = 1,

    /// <summary>The vote is not in a state that allows this action.</summary>
    WrongState = 2,

    /// <summary>The action's own arguments were rejected.</summary>
    Invalid = 3
}

/// <summary>The outcome of submitting a ballot.</summary>
internal enum BallotSubmissionOutcome
{
    /// <summary>Cast or changed; the history row and audit entry are written.</summary>
    Recorded = 0,

    /// <summary>No such vote.</summary>
    NotFound = 1,

    /// <summary>The submitter is not on this vote's roster, so they hold no ballot to cast.</summary>
    NotOnRoster = 2,

    /// <summary>The vote is not open, or its closing time has passed.</summary>
    VoteNotOpen = 3,

    /// <summary>The ballot itself was malformed — duplicate ranks, unknown option keys, wrong kind.</summary>
    InvalidBallot = 4
}

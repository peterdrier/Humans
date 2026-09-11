using System.Text.Json.Serialization;

using NodaTime;

namespace Humans.Governance.Services.Dtos;

/// <summary>
/// The outcome of an assembly vote, computed once at close and stored on the vote as
/// <c>ResultJson</c>. The results page renders the stored copy, so a later change to the
/// counting code can never restate a decision the association already took.
/// </summary>
/// <param name="Official">The binding result: Asociados and Board members only.</param>
/// <param name="Indicative">
/// The wider community's separate, advisory result. Null when the vote had no indicative
/// audience. Never merged into <paramref name="Official"/>.
/// </param>
/// <param name="Method">The counting method, named on the page so members can check it by hand.</param>
/// <param name="ComputedAt">When the result was computed.</param>
[method: JsonConstructor]
internal sealed record AssemblyVoteResult(
    AssemblyVoteAudienceResult Official,
    AssemblyVoteAudienceResult? Indicative,
    AssemblyVoteMethod Method,
    Instant ComputedAt);

/// <summary>One audience's tally. The same shape for official and indicative ballots.</summary>
/// <param name="RosterSize">How many people this audience's roster held.</param>
/// <param name="BallotsCast">How many of them cast a ballot.</param>
/// <param name="YesNo">The for/against/abstain counts; null on a RankedChoice vote.</param>
/// <param name="Rounds">The instant-runoff rounds; null on a YesNo vote.</param>
/// <param name="WinnerKey">The winning option key on a RankedChoice vote; null on a tie.</param>
/// <param name="Verdict">The outcome. A tie is reported, never resolved.</param>
/// <param name="Notes">
/// Tie-break steps actually applied, in the order applied, so the page can disclose how a
/// close elimination was decided. Structured, not prose: the results page is member-facing
/// and renders these in the viewer's culture.
/// </param>
[method: JsonConstructor]
internal sealed record AssemblyVoteAudienceResult(
    int RosterSize,
    int BallotsCast,
    YesNoTally? YesNo,
    IReadOnlyList<InstantRunoffRound>? Rounds,
    string? WinnerKey,
    AssemblyVoteVerdict Verdict,
    IReadOnlyList<AssemblyVoteNote> Notes);

/// <summary>
/// A YesNo tally. Abstentions are counted and shown but are neither for nor against
/// (statutes Art. 10.2).
/// </summary>
[method: JsonConstructor]
internal sealed record YesNoTally(int Yes, int No, int Abstain);

/// <summary>
/// One instant-runoff round.
/// </summary>
/// <param name="Number">1-based round number.</param>
/// <param name="Counts">First-preference count per still-continuing option.</param>
/// <param name="Exhausted">Ballots that no longer rank any continuing option, plus abstentions.</param>
/// <param name="EliminatedKey">The option eliminated at the end of this round; null in the deciding round.</param>
/// <param name="WinnerKey">The option that reached a majority in this round, if any.</param>
[method: JsonConstructor]
internal sealed record InstantRunoffRound(
    int Number,
    IReadOnlyDictionary<string, int> Counts,
    int Exhausted,
    string? EliminatedKey,
    string? WinnerKey);


/// <summary>
/// One counting note, stored structurally so the members' results page can render it in the
/// viewer's culture. Storing the English sentence instead would freeze it into
/// <c>ResultJson</c> for the life of the record and show English to the five other cultures.
/// </summary>
/// <param name="Kind">Which note this is; the rendered sentence comes from the resx.</param>
/// <param name="Round">The round it applies to; 0 when the note is not about one round.</param>
/// <param name="TiedCount">How many options were tied; 0 when the note is not about a tie.</param>
[method: JsonConstructor]
internal sealed record AssemblyVoteNote(AssemblyVoteNoteKind Kind, int Round, int TiedCount)
{
    /// <summary>
    /// The <c>GovernanceResource</c> key that renders this note. Every key is a literal here
    /// so a rename or a missing culture is caught by the resx parity tests rather than
    /// surfacing as a raw key on a results page.
    /// </summary>
    public string ResourceKey => Kind switch
    {
        AssemblyVoteNoteKind.NoMajority => "Votes_Note_NoMajority",
        AssemblyVoteNoteKind.DecidingRoundLevel => "Votes_Note_DecidingRoundLevel",
        AssemblyVoteNoteKind.TieBrokenByPreviousRound => "Votes_Note_TieBrokenByPreviousRound",
        _ => "Votes_Note_TieBrokenByAuthoredOrder"
    };
}

/// <summary>The counting notes the instant-runoff pass can record.</summary>
internal enum AssemblyVoteNoteKind
{
    /// <summary>One option was left and it still held no majority: nothing to declare.</summary>
    NoMajority = 0,

    /// <summary>Two options, equal counts: a tie, not an elimination (statutes Art. 10.2).</summary>
    DecidingRoundLevel = 1,

    /// <summary>An elimination tie broken by fewest votes in the previous round.</summary>
    TieBrokenByPreviousRound = 2,

    /// <summary>An elimination tie broken by authored option order.</summary>
    TieBrokenByAuthoredOrder = 3
}

/// <summary>How the ballots were counted.</summary>
internal enum AssemblyVoteMethod
{
    /// <summary>For / against / abstain against a simple or two-thirds majority.</summary>
    YesNo = 0,

    /// <summary>Instant-runoff with visible elimination rounds.</summary>
    InstantRunoff = 1
}

/// <summary>
/// The outcome of a vote. <see cref="Tie"/> is terminal for the system: statutes Art. 10.2
/// gives the President the casting vote, and the Secretary records it in the acta.
/// </summary>
internal enum AssemblyVoteVerdict
{
    /// <summary>The motion carried, or an option reached a majority.</summary>
    Passed = 0,

    /// <summary>The motion did not carry.</summary>
    Failed = 1,

    /// <summary>Tied. Never resolved in-system.</summary>
    Tie = 2
}

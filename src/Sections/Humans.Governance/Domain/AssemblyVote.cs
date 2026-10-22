using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// A recorded vote under embargo, not a secret ballot: every ballot is attributable and
/// changeable until close, and no read path exposes the tally before close except the audited
/// Admin peek. The secret-ballot counterpart lives in Humans.Surveys.
/// </summary>
internal sealed class AssemblyVote
{
    public Guid Id { get; init; }

    public GovernanceLocalizedText Title { get; set; } = GovernanceLocalizedText.Empty;

    /// <summary>
    /// Per-culture official text of the motion (Markdown, rendered through the shared
    /// sanitizer). The <see cref="OfficialCulture"/> entry is the binding one.
    /// </summary>
    public GovernanceLocalizedText OfficialText { get; set; } = GovernanceLocalizedText.Empty;

    /// <summary>
    /// The culture whose <see cref="OfficialText"/> is legally binding; every other culture's
    /// text is a translation and is labelled as such.
    /// </summary>
    public string OfficialCulture { get; set; } = string.Empty;

    public string? InfoUrl { get; set; }

    /// <summary>Ballot shape. Immutable once Open.</summary>
    public AssemblyVoteKind Kind { get; set; }

    /// <summary>Majority needed to pass. Immutable once Open.</summary>
    public RequiredMajority RequiredMajority { get; set; }

    /// <summary>Who may cast an indicative ballot. Immutable once Open.</summary>
    public IndicativeAudience IndicativeAudience { get; set; }

    /// <summary>How far individual ballots are disclosed after close. Immutable once Open.</summary>
    public BallotDisclosure BallotDisclosure { get; set; }

    public AssemblyVoteStatus Status { get; set; } = AssemblyVoteStatus.Draft;

    /// <summary>The assembly this vote belongs to, for the acta. Not a link to an entity.</summary>
    public LocalDate? AssemblyDate { get; set; }

    /// <summary>
    /// The announced closing time. Extendable while Open; a vote whose
    /// <c>ClosesAt</c> has passed is Closed for every purpose, whether or not the
    /// lapse job has stamped <see cref="ClosedAt"/> yet.
    /// </summary>
    public Instant ClosesAt { get; set; }

    public Instant? OpenedAt { get; set; }

    public Guid? OpenedByUserId { get; set; }

    public Instant? ClosedAt { get; set; }

    /// <summary>The Admin who stopped the vote; null when it lapsed at <see cref="ClosesAt"/>.</summary>
    public Guid? ClosedByUserId { get; set; }

    /// <summary>Required when cancelling; the reason goes in the audit entry and the acta.</summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// The result computed once at close, serialized so it stays stable even if the
    /// counting code changes later. The stored result is what the results page renders.
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// Settable only so an account merge can repoint it at the surviving account, since it is
    /// the same human.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Authored options; RankedChoice only. Aggregate-local navigation.</summary>
    public ICollection<AssemblyVoteOption> Options { get; } = [];

    /// <summary>
    /// True when a ballot may be cast or changed at <paramref name="now"/>. The embargo and the
    /// ballot gate both read this, so a lapsed vote stops accepting ballots on its deadline
    /// rather than when the hourly job gets to it.
    /// </summary>
    public bool AcceptsBallotsAt(Instant now) =>
        Status == AssemblyVoteStatus.Open && now < ClosesAt;
}

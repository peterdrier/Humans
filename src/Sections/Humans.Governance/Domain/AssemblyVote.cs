using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// A binding vote of the association: Asociados (and Board members, who are de facto
/// Asociados) voting on a motion during or around a General Assembly, optionally with
/// indicative participation by the wider community.
/// </summary>
/// <remarks>
/// A <em>recorded vote under embargo</em>, not a secret ballot: every ballot is attributable
/// and changeable until close, while no read path exposes the tally before close except the
/// audited Admin peek. The secret-ballot counterpart lives in Humans.Surveys — see
/// <c>Docs/features/assembly-votes.md</c>, "Why not Surveys".
/// </remarks>
internal sealed class AssemblyVote
{
    /// <summary>Unique identifier for the vote.</summary>
    public Guid Id { get; init; }

    /// <summary>Per-culture title.</summary>
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

    /// <summary>Optional link to supporting information.</summary>
    public string? InfoUrl { get; set; }

    /// <summary>Ballot shape. Immutable once Open.</summary>
    public AssemblyVoteKind Kind { get; set; }

    /// <summary>Majority needed to pass. Immutable once Open.</summary>
    public RequiredMajority RequiredMajority { get; set; }

    /// <summary>Who may cast an indicative ballot. Immutable once Open.</summary>
    public IndicativeAudience IndicativeAudience { get; set; }

    /// <summary>How far individual ballots are disclosed after close. Immutable once Open.</summary>
    public BallotDisclosure BallotDisclosure { get; set; }

    /// <summary>Lifecycle state.</summary>
    public AssemblyVoteStatus Status { get; set; } = AssemblyVoteStatus.Draft;

    /// <summary>The assembly this vote belongs to, for the acta. Not a link to an entity.</summary>
    public LocalDate? AssemblyDate { get; set; }

    /// <summary>
    /// The announced closing time. Extendable while Open; a vote whose
    /// <c>ClosesAt</c> has passed is Closed for every purpose, whether or not the
    /// lapse job has stamped <see cref="ClosedAt"/> yet.
    /// </summary>
    public Instant ClosesAt { get; set; }

    /// <summary>When the vote was opened and the roster frozen.</summary>
    public Instant? OpenedAt { get; set; }

    /// <summary>The Admin who opened the vote. Bare cross-section reference — no nav.</summary>
    public Guid? OpenedByUserId { get; set; }

    /// <summary>When the vote actually closed.</summary>
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
    /// The Board member or Admin who drafted the vote. Bare reference — no nav. Settable for
    /// the same single reason as the other actor columns: an account merge repoints it at the
    /// surviving account, since it is the same human. Never reassigned otherwise.
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>When the draft was created.</summary>
    public Instant CreatedAt { get; init; }

    /// <summary>When the vote was last modified.</summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>Authored options; RankedChoice only. Aggregate-local navigation.</summary>
    public ICollection<AssemblyVoteOption> Options { get; } = [];

    /// <summary>True once the vote has reached a terminal state.</summary>
    public bool IsTerminal =>
        Status is AssemblyVoteStatus.Closed or AssemblyVoteStatus.Cancelled;

    /// <summary>
    /// True when the vote should be treated as closed at <paramref name="now"/> — either it
    /// already is, or it is Open and its deadline has passed. The embargo and the ballot
    /// gate both read this, so a lapsed vote never accepts a ballot nor hides its result
    /// while waiting for the hourly job.
    /// </summary>
    public bool IsClosedAt(Instant now) =>
        Status == AssemblyVoteStatus.Closed ||
        (Status == AssemblyVoteStatus.Open && now >= ClosesAt);

    /// <summary>True when a ballot may be cast or changed at <paramref name="now"/>.</summary>
    public bool AcceptsBallotsAt(Instant now) =>
        Status == AssemblyVoteStatus.Open && now < ClosesAt;
}

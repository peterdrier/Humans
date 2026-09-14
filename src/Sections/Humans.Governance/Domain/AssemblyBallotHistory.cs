using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// One recorded revision of a ballot. Append-only: the repository exposes add and read
/// only (design-rules §12), so a member's voting history can never be rewritten.
/// </summary>
internal sealed class AssemblyBallotHistory
{
    /// <summary>Unique identifier for the history row.</summary>
    public Guid Id { get; init; }

    /// <summary>The ballot this revision belongs to.</summary>
    public Guid BallotId { get; init; }

    /// <summary>Navigation to the ballot.</summary>
    public AssemblyBallot Ballot { get; set; } = null!;

    /// <summary>The revision number this row records.</summary>
    public int Revision { get; init; }

    /// <summary>The answer as recorded at this revision.</summary>
    public AssemblyBallotChoice Choice { get; init; }

    /// <summary>The preference order as recorded at this revision; RankedChoice only.</summary>
    public IReadOnlyList<string>? Ranking { get; init; }

    /// <summary>When this revision was recorded.</summary>
    public Instant RecordedAt { get; init; }
}

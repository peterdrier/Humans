using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// One recorded revision of a ballot. Append-only: the repository exposes add and read
/// only (design-rules §12), so a member's voting history can never be rewritten.
/// </summary>
internal sealed class AssemblyBallotHistory
{
    public Guid Id { get; init; }

    public Guid BallotId { get; init; }

    public AssemblyBallot Ballot { get; set; } = null!;

    public int Revision { get; init; }

    public AssemblyBallotChoice Choice { get; init; }

    public IReadOnlyList<string>? Ranking { get; init; }

    public Instant RecordedAt { get; init; }
}

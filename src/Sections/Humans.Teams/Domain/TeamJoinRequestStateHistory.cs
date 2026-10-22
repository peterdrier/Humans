using NodaTime;
using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

/// <summary>Append-only.</summary>
internal sealed class TeamJoinRequestStateHistory
{
    public Guid Id { get; init; }

    public Guid TeamJoinRequestId { get; init; }

    public TeamJoinRequest TeamJoinRequest { get; set; } = null!;

    /// <summary>The status after this transition.</summary>
    public TeamJoinRequestStatus Status { get; init; }

    public Instant ChangedAt { get; init; }

    public Guid ChangedByUserId { get; init; }

    public string? Notes { get; init; }
}

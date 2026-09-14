using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Domain;

/// <summary>
/// Audit record of application state transitions.
/// </summary>
internal sealed class ApplicationStateHistory
{
    public Guid Id { get; init; }

    public Guid ApplicationId { get; init; }

    public Application Application { get; set; } = null!;

    public ApplicationStatus Status { get; init; }

    public Instant ChangedAt { get; init; }

    /// <summary>
    /// ID of the user who made the change. Use <c>IUserService</c> to
    /// hydrate display info — cross-domain navigation properties are
    /// forbidden on this entity (design-rules §6).
    /// </summary>
    public Guid ChangedByUserId { get; init; }

    public string? Notes { get; init; }
}

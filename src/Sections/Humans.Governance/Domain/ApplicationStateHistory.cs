using Humans.Governance.Contracts;
using NodaTime;

namespace Humans.Governance.Domain;

internal sealed class ApplicationStateHistory
{
    public Guid Id { get; init; }

    public Guid ApplicationId { get; init; }

    public Application Application { get; set; } = null!;

    public ApplicationStatus Status { get; init; }

    public Instant ChangedAt { get; init; }

    public Guid ChangedByUserId { get; init; }

    public string? Notes { get; init; }
}

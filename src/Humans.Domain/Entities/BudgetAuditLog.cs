using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Append-only audit log for budget changes. No UPDATE or DELETE allowed.
/// </summary>
public class BudgetAuditLog
{
    public Guid Id { get; init; }
    public Guid BudgetYearId { get; init; }
    public BudgetYear? BudgetYear { get; set; }
    public string EntityType { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public string? FieldName { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string Description { get; init; } = string.Empty;
    public Guid ActorUserId { get; init; }
    public User? ActorUser { get; set; }
    public Instant OccurredAt { get; init; }
}

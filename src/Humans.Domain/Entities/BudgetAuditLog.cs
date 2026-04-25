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

    /// <summary>
    /// Cross-domain navigation to the actor user. Do not use — callers resolve
    /// display names via <see cref="Humans.Application.Interfaces.IUserService"/>
    /// / <c>human-link</c> tag helper, keyed off <see cref="ActorUserId"/>.
    /// Retained only so EF's configured relationship keeps the FK constraint.
    /// </summary>
    [Obsolete("Cross-domain nav. Use ActorUserId + IUserService to resolve the user.")]
    public User? ActorUser { get; set; }

    public Instant OccurredAt { get; init; }
}

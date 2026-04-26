using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Links a user to a shift with a state machine tracking signup lifecycle.
/// State transitions: Pending → Confirmed/Refused/Bailed, Confirmed → Bailed/NoShow/Cancelled.
/// </summary>
public class ShiftSignup
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// FK to the volunteer.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// FK to the shift.
    /// </summary>
    public Guid ShiftId { get; set; }

    /// <summary>
    /// Current state of the signup.
    /// </summary>
    public SignupStatus Status { get; set; }

    /// <summary>
    /// Whether this signup was created via "voluntell" (enrolled by a coordinator).
    /// </summary>
    public bool Enrolled { get; set; }

    /// <summary>
    /// FK to the user who enrolled the volunteer (voluntell). Null for self-signup.
    /// </summary>
    public Guid? EnrolledByUserId { get; set; }

    /// <summary>
    /// FK to the user who approved/refused/bailed the signup.
    /// </summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>
    /// When the signup was reviewed (approved, refused, etc.).
    /// </summary>
    public Instant? ReviewedAt { get; set; }

    /// <summary>
    /// Reason for status change (refuse reason, bail reason, etc.).
    /// </summary>
    public string? StatusReason { get; set; }

    /// <summary>
    /// Shared Guid linking all signups created by a single range signup action.
    /// Null for individual event-time signups. Used by BailRangeAsync.
    /// </summary>
    public Guid? SignupBlockId { get; set; }

    /// <summary>
    /// When the signup was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the signup was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    // Navigation properties

    /// <summary>
    /// Navigation property to the shift.
    /// </summary>
    public Shift Shift { get; set; } = null!;

    // State transition methods

    /// <summary>
    /// Confirm a pending signup.
    /// </summary>
    public void Confirm(Guid reviewerUserId, IClock clock)
    {
        if (Status is not SignupStatus.Pending)
            throw new InvalidOperationException($"Cannot confirm signup in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = SignupStatus.Confirmed;
        ReviewedByUserId = reviewerUserId;
        ReviewedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Refuse a pending signup.
    /// </summary>
    public void Refuse(Guid reviewerUserId, IClock clock, string? reason)
    {
        if (Status is not SignupStatus.Pending)
            throw new InvalidOperationException($"Cannot refuse signup in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = SignupStatus.Refused;
        ReviewedByUserId = reviewerUserId;
        ReviewedAt = now;
        StatusReason = reason;
        UpdatedAt = now;
    }

    /// <summary>
    /// Bail from a confirmed or pending signup.
    /// </summary>
    public void Bail(Guid actorUserId, IClock clock, string? reason)
    {
        if (Status is not (SignupStatus.Confirmed or SignupStatus.Pending))
            throw new InvalidOperationException($"Cannot bail signup in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = SignupStatus.Bailed;
        ReviewedByUserId = actorUserId;
        ReviewedAt = now;
        StatusReason = reason;
        UpdatedAt = now;
    }

    /// <summary>
    /// Mark a confirmed signup as no-show (post-shift only).
    /// </summary>
    public void MarkNoShow(Guid reviewerUserId, IClock clock)
    {
        if (Status is not SignupStatus.Confirmed)
            throw new InvalidOperationException($"Cannot mark no-show for signup in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = SignupStatus.NoShow;
        ReviewedByUserId = reviewerUserId;
        ReviewedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Cancel a signup (system-only — shift/rota deleted, account deletion).
    /// </summary>
    public void Cancel(IClock clock, string? reason)
    {
        if (Status is not (SignupStatus.Confirmed or SignupStatus.Pending))
            throw new InvalidOperationException($"Cannot cancel signup in {Status} state");
        Status = SignupStatus.Cancelled;
        StatusReason = reason;
        UpdatedAt = clock.GetCurrentInstant();
    }

    /// <summary>
    /// Remove a confirmed signup (coordinator/admin unassignment).
    /// </summary>
    public void Remove(Guid removedByUserId, IClock clock, string? reason)
    {
        if (Status is not SignupStatus.Confirmed)
            throw new InvalidOperationException($"Cannot remove signup in {Status} state");
        var now = clock.GetCurrentInstant();
        Status = SignupStatus.Cancelled;
        ReviewedByUserId = removedByUserId;
        ReviewedAt = now;
        StatusReason = reason;
        UpdatedAt = now;
    }
}

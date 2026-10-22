using NodaTime;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Domain;

/// <summary>
/// Pending → Confirmed/Refused/Bailed/Cancelled; Confirmed → Bailed/NoShow/Cancelled.
/// </summary>
internal sealed class ShiftSignup
{
    public Guid Id { get; init; }

    public Guid UserId { get; set; }

    public Guid ShiftId { get; set; }

    public SignupStatus Status { get; set; }

    /// <summary>Created by "voluntell": a coordinator enrolled the volunteer.</summary>
    public bool Enrolled { get; set; }

    /// <summary>Null for self-signup.</summary>
    public Guid? EnrolledByUserId { get; set; }

    /// <summary>Who approved, refused or bailed the signup.</summary>
    public Guid? ReviewedByUserId { get; set; }

    public Instant? ReviewedAt { get; set; }

    /// <summary>The coordinator's refuse or bail reason.</summary>
    public string? StatusReason { get; set; }

    /// <summary>Shared by every signup from one range action, so BailRangeAsync can bail them together.</summary>
    public Guid? SignupBlockId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public Shift Shift { get; set; } = null!;

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

    /// <summary>Post-shift only.</summary>
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

    /// <summary>System-only: shift or rota deleted, account deletion.</summary>
    public void Cancel(IClock clock, string? reason)
    {
        if (Status is not (SignupStatus.Confirmed or SignupStatus.Pending))
            throw new InvalidOperationException($"Cannot cancel signup in {Status} state");
        Status = SignupStatus.Cancelled;
        StatusReason = reason;
        UpdatedAt = clock.GetCurrentInstant();
    }

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

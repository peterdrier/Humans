using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// A request to merge two user accounts.
/// Created when a user verifies an email that belongs to another account.
/// The source account's data is migrated to the target account on acceptance.
/// </summary>
public class AccountMergeRequest
{
    /// <summary>
    /// Unique identifier for this merge request.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// The user requesting the merge (the account that will receive the data).
    /// </summary>
    public Guid TargetUserId { get; init; }

    /// <summary>
    /// Navigation property to the target user.
    /// </summary>
    public User TargetUser { get; set; } = null!;

    /// <summary>
    /// The user whose account will be archived (the account that owns the conflicting email).
    /// </summary>
    public Guid SourceUserId { get; init; }

    /// <summary>
    /// Navigation property to the source user.
    /// </summary>
    public User SourceUser { get; set; } = null!;

    /// <summary>
    /// The email address that triggered the merge request.
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The pending (unverified) email record on the target user's account.
    /// Will be verified and kept on acceptance, or removed on rejection.
    /// </summary>
    public Guid PendingEmailId { get; init; }

    /// <summary>
    /// Current status of the merge request.
    /// </summary>
    public AccountMergeRequestStatus Status { get; set; }

    /// <summary>
    /// When the merge request was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the merge request was resolved (accepted or rejected).
    /// </summary>
    public Instant? ResolvedAt { get; set; }

    /// <summary>
    /// The admin who resolved the merge request.
    /// </summary>
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>
    /// Navigation property to the admin who resolved the merge request.
    /// </summary>
    public User? ResolvedByUser { get; set; }

    /// <summary>
    /// Optional notes from the admin who resolved the request.
    /// </summary>
    public string? AdminNotes { get; set; }
}

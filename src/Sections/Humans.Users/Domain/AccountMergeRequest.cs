using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// A request to merge two user accounts.
/// Created when a user verifies an email that belongs to another account.
/// The source account's data is migrated to the target account on acceptance.
/// </summary>
internal sealed class AccountMergeRequest
{
    public Guid Id { get; init; }

    public Guid TargetUserId { get; init; }

    public User TargetUser { get; set; } = null!;

    public Guid SourceUserId { get; init; }

    public User SourceUser { get; set; } = null!;

    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// The pending (unverified) email record on the target user's account.
    /// Will be verified and kept on acceptance, or removed on rejection.
    /// </summary>
    public Guid PendingEmailId { get; init; }

    public AccountMergeRequestStatus Status { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public User? ResolvedByUser { get; set; }

    public string? AdminNotes { get; set; }
}

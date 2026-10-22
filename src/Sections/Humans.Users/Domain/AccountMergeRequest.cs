using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// Raised when a user verifies an email that belongs to another account; acceptance moves the
/// source account's data to the target.
/// </summary>
internal sealed class AccountMergeRequest
{
    public Guid Id { get; init; }

    public Guid TargetUserId { get; init; }

    public User TargetUser { get; set; } = null!;

    public Guid SourceUserId { get; init; }

    public User SourceUser { get; set; } = null!;

    public string Email { get; init; } = string.Empty;

    /// <summary>The target's unverified email row: verified on acceptance, removed on rejection.</summary>
    public Guid PendingEmailId { get; init; }

    public AccountMergeRequestStatus Status { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public User? ResolvedByUser { get; set; }

    public string? AdminNotes { get; set; }
}

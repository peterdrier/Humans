using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for detecting and resolving duplicate user accounts
/// where the same email address appears on multiple User records.
/// </summary>
public interface IDuplicateAccountService
{
    /// <summary>
    /// Scans for email conflicts: multiple Users whose emails overlap
    /// across User.Email and UserEmail.Email (case-insensitive, gmail/googlemail equivalence).
    /// </summary>
    Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets detailed info about a specific duplicate group for resolution.
    /// </summary>
    Task<DuplicateAccountGroup?> GetDuplicateGroupAsync(Guid userId1, Guid userId2, CancellationToken ct = default);

    /// <summary>
    /// Resolves a duplicate by archiving the source account and re-linking
    /// its external logins to the target account.
    /// </summary>
    Task ResolveAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Guid adminUserId,
        string? notes = null,
        CancellationToken ct = default);
}

/// <summary>
/// A group of users that share an email address across User.Email and UserEmail.Email.
/// </summary>
public class DuplicateAccountGroup
{
    /// <summary>
    /// The email address that appears on multiple accounts.
    /// </summary>
    public string SharedEmail { get; init; } = string.Empty;

    /// <summary>
    /// The users involved in the conflict.
    /// </summary>
    public List<DuplicateAccountInfo> Accounts { get; init; } = [];
}

/// <summary>
/// Summary of a user involved in an email conflict.
/// </summary>
public class DuplicateAccountInfo
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? ProfilePictureUrl { get; init; }
    public string? MembershipTier { get; init; }
    public string? MembershipStatus { get; init; }
    public DateTime? LastLogin { get; init; }
    public DateTime? CreatedAt { get; init; }
    public int TeamCount { get; init; }
    public int RoleAssignmentCount { get; init; }
    public bool HasProfile { get; init; }
    public bool IsProfileComplete { get; init; }
    public List<string> EmailSources { get; init; } = [];
    public List<string> Teams { get; init; } = [];
}

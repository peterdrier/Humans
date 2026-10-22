using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>Detection only: resolution is <see cref="IAccountMergeService.MergeAsync"/>.</summary>
public interface IDuplicateAccountService : IApplicationService
{
    /// <summary>Overlaps across <c>User.Email</c> and every <c>UserEmail</c> row, case-insensitive, gmail/googlemail equivalent.</summary>
    Task<IReadOnlyList<DuplicateAccountGroup>> DetectDuplicatesAsync(CancellationToken ct = default);
}

public sealed class DuplicateAccountGroup
{
    public string SharedEmail { get; init; } = string.Empty;

    public List<DuplicateAccountInfo> Accounts { get; init; } = [];
}

public sealed class DuplicateAccountInfo
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

namespace Humans.GoogleIntegration.Services.Workspace;

internal interface IGoogleDirectoryClient
{
    Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default);

    /// <summary>Directory, not Cloud Identity: it returns the member count and display name the all-groups page shows.</summary>
    Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default);
}

/// <summary>Exactly one of <see cref="Users"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DirectoryUserListResult(
    IReadOnlyList<DirectoryUser>? Users,
    GoogleClientError? Error);

internal sealed record DirectoryUser(string PrimaryEmail);

/// <summary>Exactly one of <see cref="Groups"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DirectoryGroupListResult(
    IReadOnlyList<DirectoryGroup>? Groups,
    GoogleClientError? Error);

/// <summary>The connector drops rows without an id or email, so both are always set.</summary>
internal sealed record DirectoryGroup(
    string Id,
    string Email,
    string? DisplayName,
    long? DirectMembersCount);

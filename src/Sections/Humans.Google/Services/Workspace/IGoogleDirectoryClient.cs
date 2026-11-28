namespace Humans.Google.Services.Workspace;

internal interface IGoogleDirectoryClient
{
    /// <summary>Directory, not Cloud Identity: it returns the member count and display name the all-groups page shows.</summary>
    Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default);
}

/// <summary>Exactly one of <see cref="Groups"/> or <see cref="Error"/> is non-null.</summary>
internal sealed record DirectoryGroupListResult(
    IReadOnlyList<DirectoryGroup>? Groups,
    GoogleClientError? Error);

/// <summary>The connector drops rows without an email, which is how administration addresses groups.</summary>
internal sealed record DirectoryGroup(
    string Email,
    string? DisplayName,
    long? DirectMembersCount);

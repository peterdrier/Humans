namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Narrow connector over the Google Workspace Admin Directory API scoped to
/// the domain-wide enumeration operations performed by
/// <c>GoogleWorkspaceSyncService</c> (email-mismatch detection and the admin
/// "all Google groups" page). Implementations live in
/// <c>Humans.Infrastructure</c>; the Application-layer sync service (coming
/// in §15 Part 2b, issue #575) depends only on this interface so that
/// <c>Humans.Application</c> stays free of <c>Google.Apis.*</c> imports
/// (design-rules §13).
/// </summary>
/// <remarks>
/// Distinct from <see cref="IWorkspaceUserDirectoryClient"/>: that connector
/// handles individual-account lifecycle (provision / suspend / reset) scoped
/// to a single user. This connector is for domain-wide read enumeration that
/// feeds reconciliation and drift-detection flows.
/// </remarks>
public interface IGoogleDirectoryClient
{
    /// <summary>
    /// Enumerates every user on the configured Workspace domain. Pagination
    /// handled internally. Returns shape-neutral records with only the fields
    /// the sync service needs — primary email.
    /// </summary>
    Task<DirectoryUserListResult> ListDomainUsersAsync(CancellationToken ct = default);

    /// <summary>
    /// Enumerates every group on the configured Workspace domain via the
    /// Directory API (distinct from the Cloud Identity Groups API — this
    /// endpoint returns the direct-member count and display name used by
    /// the admin "all Google groups" page).
    /// </summary>
    Task<DirectoryGroupListResult> ListDomainGroupsAsync(CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IGoogleDirectoryClient.ListDomainUsersAsync"/>.
/// Exactly one of <see cref="Users"/> or <see cref="Error"/> is non-null.
/// </summary>
public sealed record DirectoryUserListResult(
    IReadOnlyList<DirectoryUser>? Users,
    GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a Directory API user row.
/// </summary>
public sealed record DirectoryUser(string PrimaryEmail);

/// <summary>
/// Outcome of <see cref="IGoogleDirectoryClient.ListDomainGroupsAsync"/>.
/// Exactly one of <see cref="Groups"/> or <see cref="Error"/> is non-null.
/// </summary>
public sealed record DirectoryGroupListResult(
    IReadOnlyList<DirectoryGroup>? Groups,
    GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a Directory API group row.
/// </summary>
/// <param name="Id">Directory-assigned group id.</param>
/// <param name="Email">Primary email of the group.</param>
/// <param name="DisplayName">Display name shown on the group page.</param>
/// <param name="DirectMembersCount">Direct member count, when reported.</param>
public sealed record DirectoryGroup(
    string Id,
    string Email,
    string? DisplayName,
    long? DirectMembersCount);

namespace Humans.GoogleIntegration.Services.Workspace;

/// <summary>
/// Directory Members API, not Cloud Identity: only Directory adds non-Google addresses as members. Status-sensitive
/// failures (404/403/409) come back as <see cref="GoogleClientError"/>.
/// </summary>
internal interface IGoogleGroupMembershipClient
{
    /// <summary><paramref name="groupGoogleId"/> is the <c>{id}</c> in <c>groups/{id}</c>. A deleted group returns a 404 error.</summary>
    Task<GroupMembershipListResult> ListMembershipsAsync(
        string groupGoogleId,
        CancellationToken ct = default);

    /// <summary>A 409 returns <see cref="GroupMembershipMutationOutcome.AlreadyExists"/>.</summary>
    Task<GroupMembershipMutationResult> CreateMembershipAsync(
        string groupGoogleId,
        string memberEmail,
        CancellationToken ct = default);

    /// <summary>Takes the resource name <see cref="ListMembershipsAsync"/> returned.</summary>
    Task<GoogleClientError?> DeleteMembershipAsync(
        string membershipResourceName,
        CancellationToken ct = default);
}

/// <summary>
/// Exactly one of <see cref="Memberships"/> or <see cref="Error"/> is non-null. Callers treat a 404 or 403 as
/// "group gone" and stop reconciling.
/// </summary>
internal sealed record GroupMembershipListResult(
    IReadOnlyList<GroupMembership>? Memberships,
    GoogleClientError? Error);

/// <summary><c>MemberEmail</c> is null when the member key is not an email; <c>ResourceName</c> is the delete id.</summary>
internal sealed record GroupMembership(string? MemberEmail, string ResourceName);

/// <summary>
/// "Already existed" skips the audit entry. On a per-member 403 the caller reads the error to decide whether to
/// mark the identity Rejected.
/// </summary>
internal sealed record GroupMembershipMutationResult(
    GroupMembershipMutationOutcome Outcome,
    GoogleClientError? Error);

internal enum GroupMembershipMutationOutcome
{
    Added,

    /// <summary>Google's 409: idempotent success.</summary>
    AlreadyExists,

    /// <summary><see cref="GroupMembershipMutationResult.Error"/> is populated.</summary>
    Failed
}

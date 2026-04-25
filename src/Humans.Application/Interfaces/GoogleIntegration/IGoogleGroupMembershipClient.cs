namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Narrow connector over the Google Cloud Identity Groups API scoped to the
/// group-membership operations performed by <c>GoogleWorkspaceSyncService</c>.
/// Implementations live in <c>Humans.Infrastructure</c>; the Application-layer
/// sync service (coming in §15 Part 2b, issue #575) depends only on this
/// interface so that <c>Humans.Application</c> stays free of
/// <c>Google.Apis.*</c> imports (design-rules §13).
/// </summary>
/// <remarks>
/// <para>
/// Every method returns shape-neutral DTOs defined in this namespace; callers
/// never see Google SDK types. Pagination is handled internally by the
/// connector — the application service sees a single async enumeration.
/// </para>
/// <para>
/// Error handling: transient Google API exceptions bubble up as
/// <see cref="GoogleClientError"/> results (via the <c>Result</c> pattern) for
/// the cases where the caller needs the HTTP status code (404 / 403 / 409).
/// Callers that only care about success/failure can ignore the error and
/// treat a non-null <c>Error</c> as failure.
/// </para>
/// </remarks>
public interface IGoogleGroupMembershipClient
{
    /// <summary>
    /// Enumerates every direct member of the Google Group identified by its
    /// numeric <paramref name="groupGoogleId"/>. Pagination handled internally.
    /// </summary>
    /// <param name="groupGoogleId">
    /// The numeric Cloud Identity group id (the <c>{id}</c> in <c>groups/{id}</c>).
    /// </param>
    /// <returns>
    /// A <see cref="GroupMembershipListResult"/> whose <c>Memberships</c>
    /// collection is non-null on success, or a populated <c>Error</c> on
    /// failure (e.g. HTTP 404 when the group itself has been deleted on
    /// Google's side).
    /// </returns>
    Task<GroupMembershipListResult> ListMembershipsAsync(
        string groupGoogleId,
        CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="memberEmail"/> to the group identified by
    /// <paramref name="groupGoogleId"/> with the <c>MEMBER</c> role. Returns
    /// success + <see cref="GroupMembershipMutationOutcome.Added"/> when the
    /// membership was newly created, <see cref="GroupMembershipMutationOutcome.AlreadyExists"/>
    /// when Google responded with HTTP 409 (idempotent), or a populated
    /// <c>Error</c> on any other failure.
    /// </summary>
    Task<GroupMembershipMutationResult> CreateMembershipAsync(
        string groupGoogleId,
        string memberEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the membership identified by its full Google Cloud Identity
    /// resource name (e.g. <c>groups/{groupId}/memberships/{membershipId}</c>).
    /// </summary>
    Task<GoogleClientError?> DeleteMembershipAsync(
        string membershipResourceName,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IGoogleGroupMembershipClient.ListMembershipsAsync"/>.
/// Exactly one of <see cref="Memberships"/> or <see cref="Error"/> is non-null.
/// </summary>
/// <param name="Memberships">
/// The direct members of the group, each projected to the minimum shape the
/// sync service needs — the preferred key (email) and the membership's
/// Google-assigned resource name (used later for delete).
/// </param>
/// <param name="Error">
/// Failure description, when the list failed. Callers typically treat
/// <c>StatusCode</c> 404 or 403 as "group gone" and stop further reconciliation.
/// </param>
public sealed record GroupMembershipListResult(
    IReadOnlyList<GroupMembership>? Memberships,
    GoogleClientError? Error);

/// <summary>
/// Shape-neutral projection of a single Cloud Identity group membership row.
/// </summary>
/// <param name="MemberEmail">
/// The preferred member key's id — typically the primary email. May be null
/// when the membership is keyed on something other than an email address.
/// </param>
/// <param name="ResourceName">
/// The full resource name, e.g. <c>groups/{groupId}/memberships/{membershipId}</c>.
/// Used as the id for later delete operations.
/// </param>
public sealed record GroupMembership(string? MemberEmail, string ResourceName);

/// <summary>
/// Outcome of <see cref="IGoogleGroupMembershipClient.CreateMembershipAsync"/>.
/// </summary>
/// <param name="Outcome">
/// What actually happened on Google's side. Callers use this to decide
/// whether to emit a "granted access" audit entry or skip the audit (the
/// "already existed" case).
/// </param>
/// <param name="Error">
/// Populated when the add failed for a reason other than "already exists".
/// For HTTP 403 on a per-member basis (no Google account behind the email),
/// the caller is expected to read <see cref="GoogleClientError.StatusCode"/>
/// and <see cref="GoogleClientError.RawMessage"/> to decide whether to mark
/// the user's GoogleEmailStatus as Rejected.
/// </param>
public sealed record GroupMembershipMutationResult(
    GroupMembershipMutationOutcome Outcome,
    GoogleClientError? Error);

/// <summary>
/// What happened when adding a member to a group.
/// </summary>
public enum GroupMembershipMutationOutcome
{
    /// <summary>Membership was newly added.</summary>
    Added,

    /// <summary>Google responded with HTTP 409 — the membership already existed. Treat as success (idempotent).</summary>
    AlreadyExists,

    /// <summary>Google responded with any other error. <see cref="GroupMembershipMutationResult.Error"/> is populated.</summary>
    Failed
}

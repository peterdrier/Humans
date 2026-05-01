namespace Humans.Domain.Constants;

/// <summary>
/// Event type identifiers for Google sync outbox messages.
/// </summary>
public static class GoogleSyncOutboxEventTypes
{
    public const string AddUserToTeamResources = "AddUserToTeamResources";
    public const string RemoveUserFromTeamResources = "RemoveUserFromTeamResources";

    /// <summary>
    /// Reconcile the membership of a single Google Group against the union of
    /// every <c>IGoogleGroupMembershipSource</c>'s expected member set for that
    /// group. The group email is stored in <c>DeduplicationKey</c>; <c>TeamId</c>
    /// and <c>UserId</c> are unused for this event type and set to <c>Guid.Empty</c>.
    /// </summary>
    public const string ReconcileGroupMembership = "ReconcileGroupMembership";
}

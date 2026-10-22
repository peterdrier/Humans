using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Variant 1 (loss of access) when the removed address is the member's Workspace identity or they have none; Variant 2
/// (secondary cleanup) when their identity is another address. An address with no <c>UserEmail</c> row is a no-op.
/// </summary>
internal interface IGoogleRemovalNotificationService : IApplicationService
{
    /// <summary>
    /// Call only after a successful Google delete. <paramref name="resourceIdentifier"/> (group email or Drive URL) is the
    /// name fallback and shows in Group emails. <paramref name="reason"/> is advisory and does not suppress.
    /// </summary>
    Task NotifyRemovalAsync(
        string removedEmail,
        GoogleResourceType resourceType,
        string? resourceName,
        string? resourceIdentifier,
        SyncRemovalReason reason,
        CancellationToken cancellationToken = default);
}

internal enum SyncRemovalReason
{
    Reconciliation = 0,

    /// <summary>The recipient still gets a Variant 2 email naming the old address.</summary>
    EmailRotation = 1
}

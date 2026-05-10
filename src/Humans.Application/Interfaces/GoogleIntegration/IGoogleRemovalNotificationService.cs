using Humans.Application.Interfaces;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Sends email notifications to users when Google Workspace sync removes
/// a Group membership or Drive permission belonging to one of their
/// verified addresses (issue peterdrier/Humans#639).
/// </summary>
/// <remarks>
/// <para>
/// Two copy variants exist, chosen by the recipient's current state:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Variant 1 — Loss of access</b>: the address was the user's only
///     <c>IsGoogle</c> address, or no other <c>IsGoogle</c> address remains
///     verified after the removal. Sub-templates per resource type
///     (Group / Drive).
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Variant 2 — Secondary-email cleanup</b>: the user has another
///     verified <c>IsGoogle</c> <c>UserEmail</c> row that is not also being
///     removed; the message reassures the user that their primary
///     Nobodies access is unchanged.
///     </description>
///   </item>
/// </list>
/// <para>
/// Lookup by removed address yielding no <c>UserEmail</c> row is a no-op
/// (orphan / deleted human / OAuth-rename-in-place case). The
/// <see cref="SyncRemovalReason"/> value is plumbed through for audit /
/// telemetry purposes but does not currently drive suppression — Workspace
/// identity rotation produces a Variant 2 ("secondary cleanup") email,
/// which is the desired behavior.
/// </para>
/// </remarks>
public interface IGoogleRemovalNotificationService : IApplicationService
{
    /// <summary>
    /// Notifies the owner of <paramref name="removedEmail"/> that they
    /// lost a Google Workspace permission. Safe to call after a successful
    /// Google API delete; no-op when the address is unknown, when the
    /// reason is suppressible, or when the recipient cannot be resolved.
    /// </summary>
    /// <param name="removedEmail">
    /// The address that just lost its Google permission. Variant logic
    /// keys off this value; the email is sent here.
    /// </param>
    /// <param name="resourceType">
    /// <see cref="GoogleResourceType.Group"/> or any Drive variant
    /// (<c>DriveFolder</c>, <c>SharedDrive</c>, <c>DriveFile</c>) — the
    /// latter map to the same Drive sub-template.
    /// </param>
    /// <param name="resourceName">
    /// Human-readable resource name (group name or folder/file name).
    /// Falls back to <paramref name="resourceIdentifier"/> when null/empty.
    /// </param>
    /// <param name="resourceIdentifier">
    /// Stable identifier — group email for Groups, optional URL for Drive.
    /// Used as the missing-name fallback and surfaced in the email body
    /// for Group removals.
    /// </param>
    /// <param name="reason">
    /// Why the removal happened. Currently advisory only — flows through to
    /// logs / audit but does not drive suppression (issue peterdrier/Humans#639).
    /// </param>
    Task NotifyRemovalAsync(
        string removedEmail,
        GoogleResourceType resourceType,
        string? resourceName,
        string? resourceIdentifier,
        SyncRemovalReason reason,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Why a sync removal happened. Plumbed through for audit / telemetry —
/// see <see cref="IGoogleRemovalNotificationService"/>.
/// </summary>
public enum SyncRemovalReason
{
    /// <summary>
    /// Reconciliation removed the address because the membership/permission
    /// no longer matches expected team state.
    /// </summary>
    Reconciliation = 0,

    /// <summary>
    /// The user's <c>IsGoogle</c> identity rotated from one address to
    /// another, and the old address was cleaned up immediately after the
    /// new one was granted. The recipient still gets a Variant 2 ("secondary
    /// cleanup") email confirming which address was tidied up.
    /// </summary>
    EmailRotation = 1
}

namespace Humans.Application.Interfaces;

/// <summary>
/// Shape-neutral projection of a Google Drive Activity API event, limited to
/// the fields the permission-anomaly monitor cares about. Produced by
/// <see cref="IGoogleDriveActivityClient.QueryActivityAsync"/>; consumed by
/// <c>DriveActivityMonitorService</c>. The Application layer must never see
/// <c>Google.Apis.*</c> types directly (design-rules §13).
/// </summary>
/// <param name="Actors">Every actor associated with the event, in API order.</param>
/// <param name="PermissionChange">
/// Set when the event's primary action is a permission change; <c>null</c>
/// for all other activity types. Monitor only inspects permission-change events.
/// </param>
public sealed record DriveActivityEvent(
    IReadOnlyList<DriveActivityActor> Actors,
    DriveActivityPermissionChange? PermissionChange);

/// <summary>
/// Shape-neutral projection of a Drive Activity actor. At most one of the
/// three fields is populated per actor (matching the Google API's
/// discriminated-union shape).
/// </summary>
/// <param name="KnownUserPersonName">
/// When the actor is a known user, the Drive Activity API person identifier
/// (either an email address or a <c>people/{id}</c> reference).
/// </param>
/// <param name="IsAdministrator">True when the actor is "Google Workspace Admin".</param>
/// <param name="IsSystem">True when the actor is "Google System".</param>
public sealed record DriveActivityActor(
    string? KnownUserPersonName,
    bool IsAdministrator,
    bool IsSystem);

/// <summary>
/// Shape-neutral projection of a Drive Activity permission-change action.
/// </summary>
public sealed record DriveActivityPermissionChange(
    IReadOnlyList<DriveActivityPermission> AddedPermissions,
    IReadOnlyList<DriveActivityPermission> RemovedPermissions);

/// <summary>
/// Shape-neutral projection of a single Drive Activity permission grant or
/// revocation. At most one of <see cref="UserPersonName"/>, <see cref="GroupEmail"/>,
/// <see cref="DomainName"/>, or <see cref="IsAnyone"/> is populated (matching
/// the Google API's discriminated-union shape).
/// </summary>
public sealed record DriveActivityPermission(
    string? Role,
    string? UserPersonName,
    string? GroupEmail,
    string? DomainName,
    bool IsAnyone);

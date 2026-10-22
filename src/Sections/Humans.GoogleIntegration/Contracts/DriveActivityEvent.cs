namespace Humans.GoogleIntegration.Contracts;

public sealed record DriveActivityEvent(
    IReadOnlyList<DriveActivityActor> Actors,
    DriveActivityPermissionChange? PermissionChange);

/// <summary>
/// At most one field is set per actor, as in the API's discriminated union. <paramref name="KnownUserPersonName"/>
/// is an email address or a <c>people/{id}</c> reference.
/// </summary>
public sealed record DriveActivityActor(
    string? KnownUserPersonName,
    bool IsAdministrator,
    bool IsSystem);

public sealed record DriveActivityPermissionChange(
    IReadOnlyList<DriveActivityPermission> AddedPermissions,
    IReadOnlyList<DriveActivityPermission> RemovedPermissions);

/// <summary>
/// At most one of <see cref="UserPersonName"/>, <see cref="GroupEmail"/>, <see cref="DomainName"/> or
/// <see cref="IsAnyone"/> is set.
/// </summary>
public sealed record DriveActivityPermission(
    string? Role,
    string? UserPersonName,
    string? GroupEmail,
    string? DomainName,
    bool IsAnyone);

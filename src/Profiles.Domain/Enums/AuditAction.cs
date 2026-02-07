namespace Profiles.Domain.Enums;

/// <summary>
/// Actions that can be recorded in the audit log.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum AuditAction
{
    TeamMemberAdded,
    TeamMemberRemoved,
    MemberSuspended,
    MemberUnsuspended,
    AccountAnonymized,
    RoleAssigned,
    RoleEnded,
    VolunteerApproved,
    GoogleResourceAccessGranted,
    GoogleResourceAccessRevoked,
}

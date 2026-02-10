namespace Humans.Domain.Enums;

/// <summary>
/// What triggered a Google sync action.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum GoogleSyncSource
{
    TeamMemberJoined,
    TeamMemberLeft,
    ManualSync,
    ScheduledSync,
    Suspension,
    SystemTeamSync
}

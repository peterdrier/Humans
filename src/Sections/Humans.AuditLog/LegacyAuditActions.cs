using Humans.AuditLog.Contracts;

namespace Humans.AuditLog;

/// <summary>
/// Transitional: every action the <see cref="AuditAction"/> enum still carries, declared here until its own section
/// declares it on a <see cref="AuditActionContributor"/> of its own. A section that moves its actions deletes them from
/// this class in the same PR (the registry fails startup on a name declared twice). Deleted with the enum.
/// </summary>
internal sealed class LegacyAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CampLeadAdded = new("CampLeadAdded");

    public static readonly AuditActionDefinition CampLeadRemoved = new("CampLeadRemoved");

    public static readonly AuditActionDefinition CampPrimaryLeadTransferred = new("CampPrimaryLeadTransferred");

    // No writer; stored rows may hold it.
    public static readonly AuditActionDefinition WorkspaceAccountBackupCodesInvalidated = new("WorkspaceAccountBackupCodesInvalidated");

    public static readonly AuditActionDefinition AccountMergeRequested = new("AccountMergeRequested");

    public static readonly AuditActionDefinition UserEmailProviderBackfilled = new("UserEmailProviderBackfilled");

    public static readonly AuditActionDefinition UserEmailGoogleCleared = new("UserEmailGoogleCleared");

    public static readonly AuditActionDefinition GhostExternalLoginsDeleted = new("GhostExternalLoginsDeleted");

    // No writer since the EE start date moved to Settings.
    public static readonly AuditActionDefinition CampSettingsEeStartDateChanged = new("CampSettingsEeStartDateChanged");

    // No writer since the day-off redesign; stored rows may hold it.
    public static readonly AuditActionDefinition VolunteerDayBlocked = new("VolunteerDayBlocked");

    public static readonly AuditActionDefinition VolunteerDayUnblocked = new("VolunteerDayUnblocked");

    public static readonly AuditActionDefinition VolunteerOwnBlockedDaysSaved = new("VolunteerOwnBlockedDaysSaved");

    // The daily job set DormantSince after sixty days' silence.
    public static readonly AuditActionDefinition WorkgroupDormancyFlagged = new("WorkgroupDormancyFlagged");

    // The daily job notified the Board that a flagged group is a close candidate.
    public static readonly AuditActionDefinition WorkgroupCloseCandidateFlagged = new("WorkgroupCloseCandidateFlagged");

    // An Update or a meeting that has started cleared a workgroup's dormancy inquiry.
    public static readonly AuditActionDefinition WorkgroupDormancyCleared = new("WorkgroupDormancyCleared");

    public static readonly AuditActionDefinition UserNamesBackfilled = new("UserNamesBackfilled");

}

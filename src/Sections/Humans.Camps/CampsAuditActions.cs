using Humans.AuditLog.Contracts;

namespace Humans.Camps;

/// <summary>The audit actions Camps writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CampsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CampCreated = new("CampCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampUpdated = new("CampUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampDeleted = new("CampDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonCreated = new("CampSeasonCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonApproved = new("CampSeasonApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonRejected = new("CampSeasonRejected", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonWithdrawn = new("CampSeasonWithdrawn", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonStatusChanged = new("CampSeasonStatusChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition CampNameChanged = new("CampNameChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition CampImageUploaded = new("CampImageUploaded", AuditKept.Forever);

    public static readonly AuditActionDefinition CampImageDeleted = new("CampImageDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition CampHistoricalNameAdded = new("CampHistoricalNameAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition CampHistoricalNameRemoved = new("CampHistoricalNameRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition CampImagesReordered = new("CampImagesReordered", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberRequested = new("CampMemberRequested", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberApproved = new("CampMemberApproved", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberRejected = new("CampMemberRejected", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberWithdrawn = new("CampMemberWithdrawn", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberLeft = new("CampMemberLeft", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberRemoved = new("CampMemberRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition CampMemberAddedByLead = new("CampMemberAddedByLead", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleDefinitionCreated = new("CampRoleDefinitionCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleDefinitionUpdated = new("CampRoleDefinitionUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleDefinitionDeactivated = new("CampRoleDefinitionDeactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleDefinitionReactivated = new("CampRoleDefinitionReactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleAssigned = new("CampRoleAssigned", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRoleUnassigned = new("CampRoleUnassigned", AuditKept.Forever);

    public static readonly AuditActionDefinition CampEarlyEntryGranted = new("CampEarlyEntryGranted", AuditKept.Forever);

    public static readonly AuditActionDefinition CampEarlyEntryRevoked = new("CampEarlyEntryRevoked", AuditKept.Forever);

    public static readonly AuditActionDefinition CampSeasonEeSlotCountChanged = new("CampSeasonEeSlotCountChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRegistrationOpened = new("CampRegistrationOpened", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRegistrationClosed = new("CampRegistrationClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition CampRegistrationInfoUpdated = new("CampRegistrationInfoUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampNameLockDateChanged = new("CampNameLockDateChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition CampsExported = new("CampsExported", AuditKept.Forever);
}

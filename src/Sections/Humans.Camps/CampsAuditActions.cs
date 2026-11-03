using Humans.AuditLog.Contracts;

namespace Humans.Camps;

/// <summary>The audit actions Camps writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CampsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CampCreated = new("CampCreated");

    public static readonly AuditActionDefinition CampUpdated = new("CampUpdated");

    public static readonly AuditActionDefinition CampDeleted = new("CampDeleted");

    public static readonly AuditActionDefinition CampSeasonCreated = new("CampSeasonCreated");

    public static readonly AuditActionDefinition CampSeasonApproved = new("CampSeasonApproved");

    public static readonly AuditActionDefinition CampSeasonRejected = new("CampSeasonRejected");

    public static readonly AuditActionDefinition CampSeasonWithdrawn = new("CampSeasonWithdrawn");

    public static readonly AuditActionDefinition CampSeasonStatusChanged = new("CampSeasonStatusChanged");

    public static readonly AuditActionDefinition CampNameChanged = new("CampNameChanged");

    public static readonly AuditActionDefinition CampImageUploaded = new("CampImageUploaded");

    public static readonly AuditActionDefinition CampImageDeleted = new("CampImageDeleted");

    public static readonly AuditActionDefinition CampHistoricalNameAdded = new("CampHistoricalNameAdded");

    public static readonly AuditActionDefinition CampHistoricalNameRemoved = new("CampHistoricalNameRemoved");

    public static readonly AuditActionDefinition CampImagesReordered = new("CampImagesReordered");

    public static readonly AuditActionDefinition CampMemberRequested = new("CampMemberRequested");

    public static readonly AuditActionDefinition CampMemberApproved = new("CampMemberApproved");

    public static readonly AuditActionDefinition CampMemberRejected = new("CampMemberRejected");

    public static readonly AuditActionDefinition CampMemberWithdrawn = new("CampMemberWithdrawn");

    public static readonly AuditActionDefinition CampMemberLeft = new("CampMemberLeft");

    public static readonly AuditActionDefinition CampMemberRemoved = new("CampMemberRemoved");

    public static readonly AuditActionDefinition CampMemberAddedByLead = new("CampMemberAddedByLead");

    public static readonly AuditActionDefinition CampRoleDefinitionCreated = new("CampRoleDefinitionCreated");

    public static readonly AuditActionDefinition CampRoleDefinitionUpdated = new("CampRoleDefinitionUpdated");

    public static readonly AuditActionDefinition CampRoleDefinitionDeactivated = new("CampRoleDefinitionDeactivated");

    public static readonly AuditActionDefinition CampRoleDefinitionReactivated = new("CampRoleDefinitionReactivated");

    public static readonly AuditActionDefinition CampRoleAssigned = new("CampRoleAssigned");

    public static readonly AuditActionDefinition CampRoleUnassigned = new("CampRoleUnassigned");

    public static readonly AuditActionDefinition CampEarlyEntryGranted = new("CampEarlyEntryGranted");

    public static readonly AuditActionDefinition CampEarlyEntryRevoked = new("CampEarlyEntryRevoked");

    public static readonly AuditActionDefinition CampSeasonEeSlotCountChanged = new("CampSeasonEeSlotCountChanged");

    public static readonly AuditActionDefinition CampRegistrationOpened = new("CampRegistrationOpened");

    public static readonly AuditActionDefinition CampRegistrationClosed = new("CampRegistrationClosed");

    public static readonly AuditActionDefinition CampNameLockDateChanged = new("CampNameLockDateChanged");

    public static readonly AuditActionDefinition CampsExported = new("CampsExported");
}

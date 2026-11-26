using Humans.AuditLog.Contracts;

namespace Humans.CityPlanning;

/// <summary>The audit actions CityPlanning writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CityPlanningAuditActions : AuditActionContributor
{
    // Every City Planning settings write is audited: the settings row records when a phase changed, not who.
    // Opened/Closed force the barrio placement schedule; OverrideCleared hands placement back to it.
    public static readonly AuditActionDefinition CityPlanningPlacementOpened = new("CityPlanningPlacementOpened", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningPlacementClosed = new("CityPlanningPlacementClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningPlacementOverrideCleared = new("CityPlanningPlacementOverrideCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningContainerPlacementOpened = new("CityPlanningContainerPlacementOpened", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningContainerPlacementClosed = new("CityPlanningContainerPlacementClosed", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningLimitZoneUpdated = new("CityPlanningLimitZoneUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningLimitZoneDeleted = new("CityPlanningLimitZoneDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningOfficialZonesUpdated = new("CityPlanningOfficialZonesUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningOfficialZonesDeleted = new("CityPlanningOfficialZonesDeleted", AuditKept.Forever);

    // The barrio placement schedule and the barrio registration page copy.
    public static readonly AuditActionDefinition CityPlanningPlacementDatesUpdated = new("CityPlanningPlacementDatesUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CityPlanningRegistrationInfoUpdated = new("CityPlanningRegistrationInfoUpdated", AuditKept.Forever);
}

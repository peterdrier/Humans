using Humans.AuditLog.Contracts;

namespace Humans.CityPlanning;

/// <summary>The audit actions CityPlanning writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CityPlanningAuditActions : AuditActionContributor
{
    // Every City Planning settings write is audited: the settings row records when a phase changed, not who.
    // Opened/Closed force the barrio placement schedule; OverrideCleared hands placement back to it.
    public static readonly AuditActionDefinition CityPlanningPlacementOpened = new("CityPlanningPlacementOpened");

    public static readonly AuditActionDefinition CityPlanningPlacementClosed = new("CityPlanningPlacementClosed");

    public static readonly AuditActionDefinition CityPlanningPlacementOverrideCleared = new("CityPlanningPlacementOverrideCleared");

    public static readonly AuditActionDefinition CityPlanningContainerPlacementOpened = new("CityPlanningContainerPlacementOpened");

    public static readonly AuditActionDefinition CityPlanningContainerPlacementClosed = new("CityPlanningContainerPlacementClosed");

    public static readonly AuditActionDefinition CityPlanningLimitZoneUpdated = new("CityPlanningLimitZoneUpdated");

    public static readonly AuditActionDefinition CityPlanningLimitZoneDeleted = new("CityPlanningLimitZoneDeleted");

    public static readonly AuditActionDefinition CityPlanningOfficialZonesUpdated = new("CityPlanningOfficialZonesUpdated");

    public static readonly AuditActionDefinition CityPlanningOfficialZonesDeleted = new("CityPlanningOfficialZonesDeleted");

    // The barrio placement schedule and the barrio registration page copy.
    public static readonly AuditActionDefinition CityPlanningPlacementDatesUpdated = new("CityPlanningPlacementDatesUpdated");

    public static readonly AuditActionDefinition CityPlanningRegistrationInfoUpdated = new("CityPlanningRegistrationInfoUpdated");
}

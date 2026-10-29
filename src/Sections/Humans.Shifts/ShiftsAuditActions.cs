using Humans.AuditLog.Contracts;

namespace Humans.Shifts;

/// <summary>The audit actions Shifts writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ShiftsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ShiftSignupConfirmed = new("ShiftSignupConfirmed", Verb: "confirmed signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupRefused = new("ShiftSignupRefused", Verb: "refused signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupVoluntold = new("ShiftSignupVoluntold", Verb: "voluntold", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupBailed = new("ShiftSignupBailed", Verb: "bailed", SelfVerb: "bailed from", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupNoShow = new("ShiftSignupNoShow", Verb: "marked no-show for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupCancelled = new("ShiftSignupCancelled", Verb: "removed signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition RotaMovedToTeam = new("RotaMovedToTeam");

    // Shift knobs live on a Shifts table since #2192, so the save is audited under a Shifts name.
    public static readonly AuditActionDefinition ShiftSettingsUpdated = new("ShiftSettingsUpdated");

    public static readonly AuditActionDefinition ShiftSignupCreated = new("ShiftSignupCreated", Verb: "created signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupReassigned = new("ShiftSignupReassigned", Verb: "reassigned shift signups for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition VolunteerCampSetupSet = new("VolunteerCampSetupSet");

    public static readonly AuditActionDefinition VolunteerCampSetupCleared = new("VolunteerCampSetupCleared");

    public static readonly AuditActionDefinition VolunteerDayOffMarked = new("VolunteerDayOffMarked");

    public static readonly AuditActionDefinition VolunteerDayOffCleared = new("VolunteerDayOffCleared");

    public static readonly AuditActionDefinition CoordinatorRotaMessageSent = new("CoordinatorRotaMessageSent");

    public static readonly AuditActionDefinition CoordinatorTeamRotasMessageSent = new("CoordinatorTeamRotasMessageSent");

    // On a volunteer's behalf, from the Profile build strip.
    public static readonly AuditActionDefinition VolunteerAvailabilitySet = new("VolunteerAvailabilitySet");

    public static readonly AuditActionDefinition VolunteerAvailabilityCleared = new("VolunteerAvailabilityCleared");

    public static readonly AuditActionDefinition VolunteerTrackingExported = new("VolunteerTrackingExported");
}

using Humans.AuditLog.Contracts;

namespace Humans.Shifts;

/// <summary>The audit actions Shifts writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ShiftsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ShiftSignupConfirmed = new("ShiftSignupConfirmed", AuditKept.Forever, Verb: "confirmed signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupRefused = new("ShiftSignupRefused", AuditKept.Forever, Verb: "refused signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupVoluntold = new("ShiftSignupVoluntold", AuditKept.Forever, Verb: "voluntold", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupBailed = new("ShiftSignupBailed", AuditKept.Forever, Verb: "bailed", SelfVerb: "bailed from", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupNoShow = new("ShiftSignupNoShow", AuditKept.Forever, Verb: "marked no-show for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupCancelled = new("ShiftSignupCancelled", AuditKept.Forever, Verb: "removed signup for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition RotaMovedToTeam = new("RotaMovedToTeam", AuditKept.Forever);

    // Rows written before this name existed are stored as EventSettingsUpdated.
    public static readonly AuditActionDefinition ShiftSettingsUpdated = new("ShiftSettingsUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition ShiftSignupCreated = new("ShiftSignupCreated", AuditKept.Forever, Verb: "created signup for", SelfVerb: "signed up for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition ShiftSignupReassigned = new("ShiftSignupReassigned", AuditKept.Forever, Verb: "reassigned shift signups for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition VolunteerCampSetupSet = new("VolunteerCampSetupSet", AuditKept.Forever);

    public static readonly AuditActionDefinition VolunteerCampSetupCleared = new("VolunteerCampSetupCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition VolunteerDayOffMarked = new("VolunteerDayOffMarked", AuditKept.Forever);

    public static readonly AuditActionDefinition VolunteerDayOffCleared = new("VolunteerDayOffCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition CoordinatorRotaMessageSent = new("CoordinatorRotaMessageSent", AuditKept.Forever);

    public static readonly AuditActionDefinition CoordinatorTeamRotasMessageSent = new("CoordinatorTeamRotasMessageSent", AuditKept.Forever);

    // On a volunteer's behalf, from the Profile build strip.
    public static readonly AuditActionDefinition VolunteerAvailabilitySet = new("VolunteerAvailabilitySet", AuditKept.Forever);

    public static readonly AuditActionDefinition VolunteerAvailabilityCleared = new("VolunteerAvailabilityCleared", AuditKept.Forever);

    public static readonly AuditActionDefinition VolunteerTrackingExported = new("VolunteerTrackingExported", AuditKept.Forever);
}

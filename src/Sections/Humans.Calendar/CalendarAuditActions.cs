using Humans.AuditLog.Contracts;

namespace Humans.Calendar;

/// <summary>The audit actions Calendar writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CalendarAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CalendarEventCreated = new("CalendarEventCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarEventUpdated = new("CalendarEventUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarEventDeleted = new("CalendarEventDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarOccurrenceCancelled = new("CalendarOccurrenceCancelled", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarOccurrenceOverridden = new("CalendarOccurrenceOverridden", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarFeedTokenCreated = new("CalendarFeedTokenCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CalendarFeedTokenRotated = new("CalendarFeedTokenRotated", AuditKept.Forever);
}

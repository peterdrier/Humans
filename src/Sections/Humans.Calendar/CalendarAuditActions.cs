using Humans.AuditLog.Contracts;

namespace Humans.Calendar;

/// <summary>The audit actions Calendar writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CalendarAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CalendarEventCreated = new("CalendarEventCreated");

    public static readonly AuditActionDefinition CalendarEventUpdated = new("CalendarEventUpdated");

    public static readonly AuditActionDefinition CalendarEventDeleted = new("CalendarEventDeleted");

    public static readonly AuditActionDefinition CalendarOccurrenceCancelled = new("CalendarOccurrenceCancelled");

    public static readonly AuditActionDefinition CalendarOccurrenceOverridden = new("CalendarOccurrenceOverridden");

    public static readonly AuditActionDefinition CalendarFeedTokenCreated = new("CalendarFeedTokenCreated");

    public static readonly AuditActionDefinition CalendarFeedTokenRotated = new("CalendarFeedTokenRotated");
}

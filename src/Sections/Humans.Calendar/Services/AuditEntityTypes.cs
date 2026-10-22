namespace Humans.Calendar.Services;

/// <summary>
/// Persisted strings, matched by exact equality on read. Never derive them from <c>nameof</c>: a type rename would
/// silently change what is written and queried.
/// </summary>
internal static class AuditEntityTypes
{
    public const string CalendarEvent = "CalendarEvent";
    public const string Team = "Team";
}

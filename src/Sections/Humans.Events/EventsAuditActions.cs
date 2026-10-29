using Humans.AuditLog.Contracts;

namespace Humans.Events;

/// <summary>The audit actions Events writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class EventsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition EventGuideExported = new("EventGuideExported");
}

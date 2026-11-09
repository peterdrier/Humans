using Humans.AuditLog.Contracts;

namespace Humans.Calendar.Services;

internal sealed class CalendarMutationAudit(
    IAuditLogService audit,
    ILogger<CalendarService> logger)
{
    internal async Task LogCommittedMutationAsync(
        AuditActionDefinition action, Guid eventId, string description, Guid actorUserId, Guid? owningTeamId = null)
    {
        // The mutation is committed; an audit failure must not prevent the cache refresh.
        try
        {
            await audit.LogAsync(
                action, AuditEntityTypes.CalendarEvent, eventId, description, actorUserId,
                relatedEntityId: owningTeamId,
                relatedEntityType: owningTeamId.HasValue ? AuditEntityTypes.Team : null);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Audit-log write failed AFTER {AuditAction} on calendar event {EventId} by {UserId}: {Description}. Mutation was committed; reconcile audit trail manually.",
                action, eventId, actorUserId, description);
        }
    }

}

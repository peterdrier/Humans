using Humans.AuditLog.Contracts;

namespace Humans.Email.Contracts;

/// <summary>The audit actions Email writes. Names are stored on audit rows: never rename one.</summary>
public sealed class EmailAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition FacilitatedMessageSent = new("FacilitatedMessageSent", AuditKept.Forever);

    public static readonly AuditActionDefinition CommunicationPreferenceChanged = new("CommunicationPreferenceChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition EmailDailySendCountsBackfilled = new("EmailDailySendCountsBackfilled", AuditKept.Forever);

    // One composer_self_test outbox row to the member's own address. Entity is the member.
    public static readonly AuditActionDefinition EmailComposerSelfTestSent = new("EmailComposerSelfTestSent", AuditKept.Forever);

    public static readonly AuditActionDefinition EmailOutboxRetried = new("EmailOutboxRetried", AuditKept.Forever);

    public static readonly AuditActionDefinition EmailOutboxDiscarded = new("EmailOutboxDiscarded", AuditKept.Forever);

    public static readonly AuditActionDefinition EmailSendingPaused = new("EmailSendingPaused", AuditKept.Forever);

    public static readonly AuditActionDefinition EmailSendingResumed = new("EmailSendingResumed", AuditKept.Forever);
}

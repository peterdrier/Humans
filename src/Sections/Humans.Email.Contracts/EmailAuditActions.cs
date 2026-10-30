using Humans.AuditLog.Contracts;

namespace Humans.Email.Contracts;

/// <summary>The audit actions Email writes. Names are stored on audit rows: never rename one.</summary>
public sealed class EmailAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition FacilitatedMessageSent = new("FacilitatedMessageSent");

    public static readonly AuditActionDefinition CommunicationPreferenceChanged = new("CommunicationPreferenceChanged");

    public static readonly AuditActionDefinition EmailDailySendCountsBackfilled = new("EmailDailySendCountsBackfilled");

    // One composer_self_test outbox row to the member's own address. Entity is the member.
    public static readonly AuditActionDefinition EmailComposerSelfTestSent = new("EmailComposerSelfTestSent");

    public static readonly AuditActionDefinition EmailOutboxRetried = new("EmailOutboxRetried");

    public static readonly AuditActionDefinition EmailOutboxDiscarded = new("EmailOutboxDiscarded");

    public static readonly AuditActionDefinition EmailSendingPaused = new("EmailSendingPaused");

    public static readonly AuditActionDefinition EmailSendingResumed = new("EmailSendingResumed");
}

namespace Humans.Email.Contracts;

/// <summary>
/// A rendered email for <see cref="IEmailService.SendAsync"/>. Immediate sending is not a field: it follows
/// from <see cref="TimeSensitiveTemplates"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="HtmlBody"/> is the fragment before the branded wrap.</item>
/// <item><paramref name="TemplateName"/> is stable per message type: the metrics key, and what <see cref="TimeSensitiveTemplates"/> matches on.</item>
/// <item>A null or <see cref="MessageCategory.System"/> <paramref name="Category"/> is always sent, with no unsubscribe header or footer. Any other category gets the opt-out check and unsubscribe.</item>
/// <item><paramref name="UserId"/> is the recipient human, passed by every sender; Email never resolves it from <paramref name="RecipientEmail"/>. Null only where no account stands behind the address (a signup magic link, an operator mailbox): such a message gets no opt-out check, no unsubscribe link and no outbox owner.</item>
/// <item><paramref name="CampaignId"/> feeds the campaign part of the Feedback-ID header for Google Postmaster.</item>
/// <item><paramref name="DoNotPersist"/> sends straight to the transport with no outbox row, so no retry. Only for mail to a member being erased under GDPR Article 17: a row would recreate the data the erasure just removed, and outlive it.</item>
/// <item>A null <paramref name="Culture"/> means an English footer.</item>
/// <item>Persisted enqueues that repeat a <paramref name="DeliveryId"/> reuse the existing row (first payload wins) until retention or erasure removes it.</item>
/// </list>
/// </remarks>
public sealed record EmailMessage(
    string RecipientEmail,
    string? RecipientName,
    Guid? UserId,
    string Subject,
    string HtmlBody,
    string TemplateName,
    MessageCategory? Category = null,
    string? ReplyTo = null,
    Guid? CampaignGrantId = null,
    Guid? CampaignId = null,
    bool DoNotPersist = false,
    string? Culture = null,
    Guid? DeliveryId = null);

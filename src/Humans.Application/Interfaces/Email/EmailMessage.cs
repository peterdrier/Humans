using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Email;

/// <summary>
/// A fully-rendered, ready-to-enqueue email: the recipient, the rendered content
/// (<see cref="Subject"/> + <see cref="HtmlBody"/>), and the routing facts the
/// transport needs. Built by <see cref="IEmailMessageFactory"/> — which wraps the
/// pure <see cref="IEmailRenderer"/> and stamps policy around it — and consumed by
/// the single <see cref="IEmailService.SendAsync"/> path, so opt-out, unsubscribe,
/// wrapping, metrics, and outbox routing are implemented exactly once.
/// </summary>
/// <param name="RecipientEmail">Destination address.</param>
/// <param name="RecipientName">Display name, or null.</param>
/// <param name="Subject">Rendered subject line.</param>
/// <param name="HtmlBody">Rendered HTML body fragment, before the branded wrap.</param>
/// <param name="TemplateName">Stable template key — the per-template metric key; never varies for a given message type.</param>
/// <param name="Category">
/// Opt-out category. <c>null</c> or <see cref="MessageCategory.System"/> ⇒ always
/// send: no opt-out suppression and no unsubscribe header/footer (transactional,
/// legal, and security mail). Any other category ⇒ opt-out check + unsubscribe.
/// </param>
/// <param name="ReplyTo">Reply-To address when the message routes replies (facilitated and coordinator mail).</param>
/// <param name="TriggerImmediate">When true, trigger an immediate outbox drain instead of waiting for the batch run.</param>
/// <param name="UserId">
/// Explicit recipient user id. When null the transport resolves it from
/// <see cref="RecipientEmail"/>; the campaign-code path supplies it directly
/// because the grant's user — not an email lookup — is authoritative.
/// </param>
/// <param name="CampaignGrantId">Links a campaign-code email to its grant for status tracking.</param>
public sealed record EmailMessage(
    string RecipientEmail,
    string? RecipientName,
    string Subject,
    string HtmlBody,
    string TemplateName,
    MessageCategory? Category = null,
    string? ReplyTo = null,
    bool TriggerImmediate = false,
    Guid? UserId = null,
    Guid? CampaignGrantId = null);

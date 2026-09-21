using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>
/// Transport seam for outbound email — a single entry point. Callers build a
/// fully-rendered <see cref="EmailMessage"/> in their own section's
/// <c>&lt;Section&gt;Emails</c> builder and hand it here. This service owns the one shared
/// path: opt-out suppression, unsubscribe headers, branded wrapping, outbox
/// enqueue, the per-template metric, and immediate-drain — so per-type code never
/// re-implements (or diverges on) routing policy.
/// </summary>
public interface IEmailService : IApplicationService
{
    /// <summary>
    /// Enqueues a rendered <paramref name="message"/> to the email outbox. For
    /// opt-outable categories (<see cref="EmailMessage.Category"/> non-null and not
    /// <see cref="MessageCategory.System"/>) it suppresses the send when the
    /// recipient has opted out and otherwise stamps List-Unsubscribe headers and a
    /// footer URL; it wraps the body, records the per-template metric, and triggers
    /// an immediate outbox drain when the message's
    /// <see cref="EmailMessage.TemplateName"/> is one of the
    /// <see cref="TimeSensitiveTemplates"/>. The recipient user id is taken from <see cref="EmailMessage.UserId"/>
    /// when supplied, otherwise resolved from the recipient address.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

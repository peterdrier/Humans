using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>
/// Transport seam for outbound email — a single entry point. Callers build a
/// fully-rendered <see cref="EmailMessage"/> via <see cref="IEmailMessageFactory"/>
/// (typed per message type) and hand it here. This service owns the one shared
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

/// <summary>What a working-group notice is about; picks the subject and body copy.</summary>
public enum WorkgroupNoticeKind
{
    Applied,
    Referred,
    Registered,
    Refused,
    Withdrawn,
    Ended,
    Reactivated,
    CoordinatorsChanged,
    DormancyInquiry,
    Delivered,
    DispositionRecorded
}

/// <summary>
/// Payload for a working-group register notice. <see cref="RecipientName"/> is null
/// when the recipient is a role inbox (e.g. the Board) rather than a named person.
/// </summary>
/// <param name="Detail">
/// The one variable line the notice carries: the written reasons for Refused and
/// Withdrawn, the disposition and its note for DispositionRecorded, the document
/// title for Delivered, the days of silence for DormancyInquiry. Null when the kind
/// needs none.
/// </param>
public sealed record WorkgroupNoticeRequest(
    string RecipientEmail,
    string? RecipientName,
    WorkgroupNoticeKind Kind,
    string WorkgroupName,
    string WorkgroupSlug,
    string? Detail = null,
    string? Culture = null);

using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Email;

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
    /// an immediate outbox drain when <see cref="EmailMessage.TriggerImmediate"/> is
    /// set. The recipient user id is taken from <see cref="EmailMessage.UserId"/>
    /// when supplied, otherwise resolved from the recipient address.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload for a coordinator "email a rota" message to a single signup.
/// <see cref="ShiftLines"/> are pre-rendered, chronologically-ordered shift labels
/// for the recipient on this rota (e.g. "Mon July 6 @ 19:30") — the renderer
/// HTML-encodes them, it does not parse or sort them.
/// </summary>
public record CoordinatorRotaMessageRequest(
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string RotaName,
    string MessageText,
    IReadOnlyList<string> ShiftLines,
    string? Culture = null);

/// <summary>
/// Payload for a coordinator team-level "email all rotas" message to a single signup.
/// <see cref="ShiftGroups"/> are pre-rendered, per-rota lists of chronologically-ordered
/// shift labels for the recipient (each rota's shifts in the rota's own timezone) — the
/// renderer HTML-encodes them, it does not parse or sort them.
/// </summary>
public record CoordinatorTeamRotasMessageRequest(
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string TeamName,
    string MessageText,
    IReadOnlyList<RotaShiftGroup> ShiftGroups,
    string? Culture = null);

/// <summary>
/// A recipient's shifts on a single rota, ready for the team-level coordinator
/// message renderer. <see cref="ShiftLines"/> are already chronological and
/// formatted in the rota's timezone.
/// </summary>
public sealed record RotaShiftGroup(string RotaName, IReadOnlyList<string> ShiftLines);

/// <summary>
/// Payload for enqueuing a campaign-code email.
/// </summary>
public record CampaignCodeEmailRequest(
    Guid UserId,
    Guid CampaignGrantId,
    string RecipientEmail,
    string RecipientName,
    string Subject,
    string MarkdownBody,
    string Code,
    string? ReplyTo);

/// <summary>
/// Payload for an event lifecycle notification. <see cref="NewStatus"/> picks
/// the template: <see cref="EventStatus.Pending"/> = submission received,
/// <see cref="EventStatus.Approved"/> = approved, <see cref="EventStatus.Rejected"/>
/// = rejected (requires <see cref="Reason"/> and <see cref="ActionUrl"/> for the
/// edit link), <see cref="EventStatus.ResubmitRequested"/> = changes requested
/// (also requires <see cref="Reason"/> and <see cref="ActionUrl"/>).
/// </summary>
public record EventLifecycleNotification(
    EventStatus NewStatus,
    string UserName,
    string EventTitle,
    string? Reason = null,
    string? ActionUrl = null,
    string? Culture = null)
{
    public string TemplateName() => NewStatus switch
    {
        EventStatus.Pending => "event_submitted",
        EventStatus.Approved => "event_approved",
        EventStatus.Rejected => "event_rejected",
        EventStatus.ResubmitRequested => "event_resubmit_requested",
        _ => throw new InvalidOperationException(
            $"EventLifecycleNotification does not support status {NewStatus}")
    };
}

using Humans.Users.Contracts;

namespace Humans.Email.Contracts;

/// <summary>
/// Builds a fully-rendered <see cref="EmailMessage"/> for each system email type.
/// This is the typed seam between the pure <see cref="IEmailRenderer"/> (subject +
/// HTML only) and the single <see cref="IEmailService.SendAsync"/> transport: each
/// method renders its content and stamps the routing policy (template name, opt-out
/// category, reply-to, immediate-drain, and — for campaign codes — the explicit
/// user and grant ids). Keeping one typed method per domain verb preserves
/// compile-time safety; the policy lives here, never in the renderer and never at
/// the call sites.
/// </summary>
public interface IEmailMessageFactory
{
    /// <summary>Facilitated volunteer-to-volunteer message (FacilitatedMessages); reply-to is the sender when contact info is shared.</summary>
    EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null);

    /// <summary>Working-group register notice (Governance category) — one method, the kind picks the copy.</summary>
    EmailMessage WorkgroupNotice(WorkgroupNoticeRequest request);

}

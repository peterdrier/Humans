namespace Humans.Email.Contracts;

/// <summary>
/// Builds the one email Email itself owns: the facilitated volunteer-to-volunteer relay,
/// the single template with no section vocabulary in it, sent by Users and Camps. The
/// method renders its content and stamps the routing policy (template name, opt-out
/// category, reply-to) that <see cref="IEmailService.SendAsync"/> reads. Every other
/// template belongs to the section that sends it, built by that section's own
/// <c>&lt;Section&gt;Emails</c> (memory/architecture/email-templates-live-in-sender.md,
/// peterdrier/Humans#1651) — nothing new is added here.
/// </summary>
public interface IEmailMessageFactory
{
    /// <summary>Facilitated volunteer-to-volunteer message (FacilitatedMessages); reply-to is the sender when contact info is shared.</summary>
    EmailMessage FacilitatedMessage(string recipientEmail, string recipientName, string senderName, string messageText, bool includeContactInfo, string? senderEmail, string? culture = null);
}

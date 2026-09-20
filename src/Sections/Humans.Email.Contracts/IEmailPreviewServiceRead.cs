using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Email.Contracts;

/// <summary>
/// Read-only rendering seam for displaying an always-send system email exactly as the outbox
/// would wrap it, without enqueueing or recording delivery activity.
/// </summary>
public interface IEmailPreviewServiceRead : IApplicationService
{
    /// <summary>
    /// Applies the canonical branded email wrapper to a system message.
    /// Opt-outable messages are rejected because their exact wrapper requires a recipient-specific
    /// unsubscribe URL from the send path.
    /// </summary>
    RenderedEmailPreview RenderSystemMessage(EmailMessage message);

    /// <summary>
    /// Renders a human-authored Markdown body through the same sanitized pipeline and branded
    /// wrapper the real send uses, for the compose-time "Preview" button shared by every
    /// human-composed send form. Unlike <see cref="RenderSystemMessage"/> this accepts opt-outable
    /// categories: since there is no real recipient yet, the wrapper gets a placeholder unsubscribe
    /// footer link (<c>#</c>) rather than a recipient-specific one. Creates no outbox row.
    /// </summary>
    RenderedEmailPreview RenderMarkdown(string subject, string? markdownBody, MessageCategory? category = null);
}

/// <summary>A fully wrapped, side-effect-free system email preview.</summary>
public sealed record RenderedEmailPreview(
    string RecipientEmail,
    string Subject,
    string HtmlBody);

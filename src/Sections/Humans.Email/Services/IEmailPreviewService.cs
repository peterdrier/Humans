using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Adds the Markdown preview render to <see cref="IEmailPreviewServiceRead"/>. Internal —
/// its only consumer is the section's own <c>EmailPreviewController</c>.
/// </summary>
internal interface IEmailPreviewService : IEmailPreviewServiceRead
{
    /// <summary>
    /// Renders a human-authored Markdown body through the same sanitized pipeline and branded
    /// wrapper the real send uses, for the compose-time "Preview" button shared by every
    /// human-composed send form. Unlike <see cref="IEmailPreviewServiceRead.RenderSystemMessage"/>
    /// this accepts opt-outable categories: since there is no real recipient yet, the wrapper
    /// gets a placeholder unsubscribe footer link (<c>#</c>) rather than a recipient-specific one.
    /// Creates no outbox row.
    /// </summary>
    RenderedEmailPreview RenderMarkdown(string subject, string? markdownBody, MessageCategory? category = null);
}

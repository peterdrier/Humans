using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Produces a side-effect-free preview through the same canonical body composer used by the outbox.
/// </summary>
internal sealed class EmailPreviewService(IEmailBodyComposer bodyComposer) : IEmailPreviewService
{
    /// <summary>Stands in for a recipient-specific unsubscribe URL when no real recipient exists yet.</summary>
    private const string PlaceholderUnsubscribeUrl = "#";

    public RenderedEmailPreview RenderSystemMessage(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Category is not null and not MessageCategory.System)
        {
            throw new InvalidOperationException(
                "Only always-send system emails can be previewed without recipient-specific send policy.");
        }

        var body = bodyComposer.Compose(message.HtmlBody);
        return new RenderedEmailPreview(message.RecipientEmail, message.Subject, body.HtmlBody);
    }

    public RenderedEmailPreview RenderMarkdown(string subject, string? markdownBody, MessageCategory? category = null)
    {
        var bodyHtml = SanitizedMarkdownRenderer.Render(markdownBody);
        var unsubscribeUrl = category is not null && !category.Value.IsAlwaysOn()
            ? PlaceholderUnsubscribeUrl
            : null;

        var body = bodyComposer.Compose(bodyHtml, unsubscribeUrl);
        return new RenderedEmailPreview(string.Empty, subject, body.HtmlBody);
    }
}

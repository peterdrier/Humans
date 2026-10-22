using System.Globalization;
using Humans.Base.Extensions;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal sealed class EmailPreviewService(IEmailBodyComposer bodyComposer) : IEmailPreviewManagement
{
    private const string PlaceholderUnsubscribeUrl = "#";

    public RenderedEmailPreview RenderSystemMessage(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Category is not null and not MessageCategory.System)
        {
            throw new InvalidOperationException(
                "Only always-send system emails can be previewed without recipient-specific send policy.");
        }

        var body = bodyComposer.Compose(message.HtmlBody, culture: message.Culture);
        return new RenderedEmailPreview(message.RecipientEmail, message.Subject, body.HtmlBody);
    }

    public RenderedEmailPreview RenderMarkdown(string subject, string? markdownBody, MessageCategory? category = null)
    {
        var bodyHtml = SanitizedMarkdownRenderer.Render(markdownBody);
        var unsubscribeUrl = category is not null && !category.Value.IsAlwaysOn()
            ? PlaceholderUnsubscribeUrl
            : null;

        var body = bodyComposer.Compose(bodyHtml, unsubscribeUrl, CultureInfo.CurrentUICulture.Name);
        return new RenderedEmailPreview(string.Empty, subject, body.HtmlBody);
    }
}

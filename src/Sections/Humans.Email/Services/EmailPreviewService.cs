using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Produces a side-effect-free preview through the same canonical body composer used by the outbox.
/// </summary>
internal sealed class EmailPreviewService(IEmailBodyComposer bodyComposer) : IEmailPreviewServiceRead
{
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
}

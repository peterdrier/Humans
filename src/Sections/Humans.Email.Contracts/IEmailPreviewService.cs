using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>Renders a System or uncategorized email exactly as the outbox would wrap it. Enqueues nothing.</summary>
public interface IEmailPreviewService : IApplicationService
{
    /// <summary>Rejects any other category: its wrapper may need a recipient-specific unsubscribe URL.</summary>
    RenderedEmailPreview RenderSystemMessage(EmailMessage message);
}

public sealed record RenderedEmailPreview(
    string RecipientEmail,
    string Subject,
    string HtmlBody);

using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>Renders an always-send system email exactly as the outbox would wrap it. Enqueues nothing.</summary>
public interface IEmailPreviewService : IApplicationService
{
    /// <summary>Rejects opt-outable messages: their wrapper needs a recipient-specific unsubscribe URL.</summary>
    RenderedEmailPreview RenderSystemMessage(EmailMessage message);
}

public sealed record RenderedEmailPreview(
    string RecipientEmail,
    string Subject,
    string HtmlBody);

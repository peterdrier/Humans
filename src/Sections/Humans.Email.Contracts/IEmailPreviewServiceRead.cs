using Humans.Base.Interfaces;

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
}

/// <summary>A fully wrapped, side-effect-free system email preview.</summary>
public sealed record RenderedEmailPreview(
    string RecipientEmail,
    string Subject,
    string HtmlBody);

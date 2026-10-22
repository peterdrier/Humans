using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal interface IEmailPreviewManagement : IEmailPreviewService
{
    /// <summary>
    /// Unlike <see cref="IEmailPreviewService.RenderSystemMessage"/>, accepts opt-outable categories: with no recipient
    /// yet, the footer gets a placeholder unsubscribe link. Creates no outbox row.
    /// </summary>
    RenderedEmailPreview RenderMarkdown(string subject, string? markdownBody, MessageCategory? category = null);
}

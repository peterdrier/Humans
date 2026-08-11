using Humans.Email.Contracts;

namespace Humans.Email.Models;

/// <summary>
/// The outbox dashboard at <c>/Email/EmailOutbox</c>.
/// </summary>
internal sealed class EmailOutboxViewModel
{
    public int TotalMessageCount { get; set; }
    public int QueuedCount { get; set; }
    public int SentLast24HoursCount { get; set; }
    public int FailedCount { get; set; }
    public bool IsPaused { get; set; }
    public List<EmailOutboxMessageDto> Messages { get; set; } = [];
}

/// <summary>
/// The rendered-template gallery at <c>/Email/EmailPreview</c>, one list per culture.
/// </summary>
internal sealed class EmailPreviewViewModel
{
    public Dictionary<string, List<EmailPreviewItem>> Previews { get; set; } = new(StringComparer.Ordinal);
    public string FromAddress { get; set; } = string.Empty;
}

internal sealed class EmailPreviewItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Domain;

internal sealed class EmailOutboxMessage
{
    internal const int SubjectMaxLength = 1000;

    public Guid Id { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? PlainTextBody { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }

    /// <summary>
    /// Set only on rows queued before nobodies-collective/Humans#1243; nothing reads or writes it now. The
    /// column stays until its drop is approved.
    /// </summary>
    [Obsolete("Unwritten since nobodies-collective/Humans#1243 dropped grant delivery tracking. Kept only until its column drop is approved.")]
    public Guid? CampaignGrantId { get; set; }

    public string? ReplyTo { get; set; }
    public string? ExtraHeaders { get; set; }

    /// <summary>Null on rows queued before the column existed; null and false both mean not urgent.</summary>
    public bool? Urgent { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? SentAt { get; set; }
    public Instant? PickedUpAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public Instant? NextRetryAt { get; set; }
}

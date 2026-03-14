using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class EmailOutboxMessage
{
    public Guid Id { get; set; }
    public string RecipientEmail { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string? PlainTextBody { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public Guid? CampaignGrantId { get; set; }
    public string? ReplyTo { get; set; }
    public string? ExtraHeaders { get; set; }
    public EmailOutboxStatus Status { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? SentAt { get; set; }
    public Instant? PickedUpAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public Instant? NextRetryAt { get; set; }

    // Navigation
    public User? User { get; set; }
    // CampaignGrant navigation added in Task 12 when CampaignGrant entity is created
}

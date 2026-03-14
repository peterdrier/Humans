using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class Campaign
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string EmailSubject { get; set; } = string.Empty;
    public string EmailBodyTemplate { get; set; } = string.Empty;
    public string? ReplyToAddress { get; set; }
    public CampaignStatus Status { get; set; }
    public Instant CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    // Navigation
    public User CreatedByUser { get; set; } = null!;
    public ICollection<CampaignCode> Codes { get; } = new List<CampaignCode>();
    public ICollection<CampaignGrant> Grants { get; } = new List<CampaignGrant>();
}

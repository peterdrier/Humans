using NodaTime;

namespace Humans.Campaigns.Domain;

internal sealed class CampaignCode
{
    public const int CodeMaxLength = 200;

    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string Code { get; set; } = string.Empty;
    public int ImportOrder { get; set; }
    public Instant ImportedAt { get; set; }

    public Campaign Campaign { get; set; } = null!;
    public CampaignGrant? Grant { get; set; }
}

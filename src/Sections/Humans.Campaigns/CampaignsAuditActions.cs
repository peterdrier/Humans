using Humans.AuditLog.Contracts;

namespace Humans.Campaigns;

/// <summary>The audit actions Campaigns writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CampaignsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated");

    public static readonly AuditActionDefinition CampaignUpdated = new("CampaignUpdated");

    public static readonly AuditActionDefinition CampaignActivated = new("CampaignActivated");

    public static readonly AuditActionDefinition CampaignCompleted = new("CampaignCompleted");

    public static readonly AuditActionDefinition CampaignCodesImported = new("CampaignCodesImported");

    public static readonly AuditActionDefinition CampaignWaveSent = new("CampaignWaveSent");

    public static readonly AuditActionDefinition CampaignGrantResent = new("CampaignGrantResent");
}

using Humans.Audit.Contracts;

namespace Humans.Campaigns;

/// <summary>The audit actions Campaigns writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CampaignsAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CampaignCreated = new("CampaignCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignUpdated = new("CampaignUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignActivated = new("CampaignActivated", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignCompleted = new("CampaignCompleted", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignCodesImported = new("CampaignCodesImported", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignWaveSent = new("CampaignWaveSent", AuditKept.Forever);

    public static readonly AuditActionDefinition CampaignGrantResent = new("CampaignGrantResent", AuditKept.Forever);
}

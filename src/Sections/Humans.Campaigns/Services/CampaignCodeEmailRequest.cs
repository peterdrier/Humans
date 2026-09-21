namespace Humans.Campaigns.Services;

/// <summary>
/// Payload for enqueuing a campaign-code email. Internal to Campaigns: only
/// <see cref="CampaignService"/> builds one and only <see cref="CampaignsEmails"/>
/// reads it (peterdrier/Humans#1651).
/// </summary>
internal sealed record CampaignCodeEmailRequest(
    Guid UserId,
    Guid CampaignGrantId,
    Guid CampaignId,
    string RecipientEmail,
    string RecipientName,
    string Subject,
    string MarkdownBody,
    string Code,
    string? ReplyTo);

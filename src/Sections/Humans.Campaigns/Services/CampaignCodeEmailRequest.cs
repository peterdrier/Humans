namespace Humans.Campaigns.Services;

internal sealed record CampaignCodeEmailRequest(
    Guid UserId,
    Guid CampaignId,
    string RecipientEmail,
    string RecipientName,
    string Subject,
    string MarkdownBody,
    string Code,
    string? ReplyTo);

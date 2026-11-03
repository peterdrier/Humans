namespace Humans.Campaigns.Models;

internal sealed record CampaignFormValues(
    string? Title,
    string? Description,
    string? EmailSubject,
    string? EmailBodyTemplate,
    string? ReplyToAddress);

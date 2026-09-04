using Humans.Campaigns.Services.Dtos;

namespace Humans.Campaigns.Models;

internal sealed class CampaignDetailViewModel
{
    public required CampaignAdminSummary Campaign { get; init; }
    public required CampaignDetailStatsDto Stats { get; init; }
}

/// <summary>Field values for the shared Create/Edit form partial (_CampaignFormFields).</summary>
internal sealed record CampaignFormValues(
    string? Title,
    string? Description,
    string? EmailSubject,
    string? EmailBodyTemplate,
    string? ReplyToAddress);

internal sealed class CampaignSendWaveViewModel
{
    public required CampaignAdminSummary Campaign { get; init; }
    public required IReadOnlyList<CampaignTeamOptionDto> Teams { get; init; }
    public Guid? SelectedTeamId { get; init; }
    public WaveSendPreview? Preview { get; init; }
}

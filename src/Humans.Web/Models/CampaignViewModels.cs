using Humans.Application.DTOs;
using Humans.Application.Interfaces.Campaigns;

namespace Humans.Web.Models;

public class CampaignDetailViewModel
{
    public required CampaignAdminSummary Campaign { get; init; }
    public required CampaignDetailStatsDto Stats { get; init; }
}

public class CampaignSendWaveViewModel
{
    public required CampaignAdminSummary Campaign { get; init; }
    public required IReadOnlyList<CampaignTeamOptionDto> Teams { get; init; }
    public Guid? SelectedTeamId { get; init; }
    public WaveSendPreview? Preview { get; init; }
}

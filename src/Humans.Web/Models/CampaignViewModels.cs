using Humans.Application.DTOs;
using Humans.Application.Interfaces.Campaigns;
using Humans.Domain.Entities;

namespace Humans.Web.Models;

public class CampaignDetailViewModel
{
    public required Campaign Campaign { get; init; }
    public required CampaignDetailStatsDto Stats { get; init; }
}

public class CampaignSendWaveViewModel
{
    public required Campaign Campaign { get; init; }
    public required IReadOnlyList<CampaignTeamOptionDto> Teams { get; init; }
    public Guid? SelectedTeamId { get; init; }
    public WaveSendPreview? Preview { get; init; }
}

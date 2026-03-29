using Humans.Domain.Entities;

namespace Humans.Application.DTOs;

public record CampaignDetailStatsDto(
    int TotalCodes,
    int AvailableCodes,
    int SentCount,
    int FailedCount,
    int CodesRedeemed,
    int TotalGrants);

public record CampaignDetailPageDto(
    Campaign Campaign,
    CampaignDetailStatsDto Stats);

public record CampaignTeamOptionDto(
    Guid Id,
    string Name);

public record CampaignSendWavePageDto(
    Campaign Campaign,
    IReadOnlyList<CampaignTeamOptionDto> Teams,
    Guid? SelectedTeamId,
    Interfaces.WaveSendPreview? Preview);

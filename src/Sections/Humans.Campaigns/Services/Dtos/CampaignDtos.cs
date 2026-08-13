using Humans.Campaigns.Contracts;
using Humans.Campaigns.Domain;
using NodaTime;

namespace Humans.Campaigns.Services.Dtos;

internal sealed record CampaignDetailStatsDto(
    int TotalCodes,
    int AvailableCodes,
    int SentCount,
    int FailedCount,
    int CodesRedeemed,
    int TotalGrants);

internal sealed record CampaignDetailPageDto(
    CampaignAdminSummary Campaign,
    CampaignDetailStatsDto Stats);

internal sealed record CampaignListSummary(
    Guid Id,
    string Title,
    CampaignStatus Status,
    int TotalCodes,
    int AssignedCodes,
    int SentCount,
    int FailedCount,
    Instant CreatedAt);

internal sealed record CampaignEditSnapshot(
    Guid Id,
    string Title,
    string? Description,
    string EmailSubject,
    string EmailBodyTemplate,
    string? ReplyToAddress,
    CampaignStatus Status);

internal sealed record CampaignAdminSummary(
    Guid Id,
    string Title,
    string? Description,
    CampaignStatus Status,
    IReadOnlyList<CampaignGrantSummary> Grants);

internal sealed record CampaignTeamOptionDto(
    Guid Id,
    string Name);

internal sealed record CampaignSendWavePageDto(
    CampaignAdminSummary Campaign,
    IReadOnlyList<CampaignTeamOptionDto> Teams,
    Guid? SelectedTeamId,
    WaveSendPreview? Preview);

internal sealed record WaveSendPreview(
    int EligibleCount,
    int AlreadyGrantedExcluded,
    int CodesAvailable,
    int CodesRemainingAfterSend);

internal sealed record CampaignGenerateCodesResult(
    bool Success,
    string? ErrorKey = null,
    int GeneratedCount = 0);

internal sealed record CampaignCreateResult(
    bool Success,
    Campaign? Campaign = null,
    string? ErrorKey = null);

internal sealed record CampaignUpdateResult(
    bool Success,
    string? ErrorKey = null);

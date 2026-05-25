using Humans.Application.Interfaces.Campaigns;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs;

public record CampaignDetailStatsDto(
    int TotalCodes,
    int AvailableCodes,
    int SentCount,
    int FailedCount,
    int CodesRedeemed,
    int TotalGrants);

public record CampaignDetailPageDto(
    CampaignAdminSummary Campaign,
    CampaignDetailStatsDto Stats);

public record CampaignListSummary(
    Guid Id,
    string Title,
    CampaignStatus Status,
    int TotalCodes,
    int AssignedCodes,
    int SentCount,
    int FailedCount,
    NodaTime.Instant CreatedAt);

public record CampaignEditSnapshot(
    Guid Id,
    string Title,
    string? Description,
    string EmailSubject,
    string EmailBodyTemplate,
    string? ReplyToAddress,
    CampaignStatus Status);

public record CampaignAdminSummary(
    Guid Id,
    string Title,
    string? Description,
    CampaignStatus Status,
    IReadOnlyList<CampaignGrantSummary> Grants);

public record CampaignTeamOptionDto(
    Guid Id,
    string Name);

public record CampaignSendWavePageDto(
    CampaignAdminSummary Campaign,
    IReadOnlyList<CampaignTeamOptionDto> Teams,
    Guid? SelectedTeamId,
    WaveSendPreview? Preview);

/// <summary>
/// Aggregated code-tracking data sourced from the Campaigns section.
/// Contains a per-campaign summary and a flat list of grant details
/// (including recipient user IDs/display names) for the Tickets admin
/// dashboard. Callers correlate redemptions against ticket orders.
/// </summary>
public record CampaignCodeTrackingData(
    IReadOnlyList<CampaignCodeTrackingSummary> Campaigns,
    IReadOnlyList<CampaignCodeTrackingGrant> Grants);

public record CampaignCodeTrackingSummary(
    Guid CampaignId,
    string CampaignTitle,
    int TotalGrants,
    int Redeemed);

public record CampaignCodeTrackingGrant(
    Guid GrantId,
    Guid CampaignId,
    string CampaignTitle,
    Guid UserId,
    string RecipientName,
    string? Code,
    NodaTime.Instant? RedeemedAt,
    string? LatestEmailStatus);

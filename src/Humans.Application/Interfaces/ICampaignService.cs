using Humans.Application.DTOs;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces;

public record WaveSendPreview(
    int EligibleCount,
    int AlreadyGrantedExcluded,
    int UnsubscribedExcluded,
    int CodesAvailable,
    int CodesRemainingAfterSend);

/// <summary>
/// A discount-code redemption discovered by ticket sync — code string and the
/// instant the redeeming ticket order was purchased.
/// </summary>
public record DiscountCodeRedemption(string Code, Instant RedeemedAt);

public interface ICampaignService
{
    Task<Campaign> CreateAsync(string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        Guid createdByUserId, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid id, string title, string? description,
        string emailSubject, string emailBodyTemplate, string? replyToAddress,
        CancellationToken ct = default);
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Campaign>> GetAllAsync(CancellationToken ct = default);
    Task<CampaignDetailPageDto?> GetDetailPageAsync(Guid id, CancellationToken ct = default);
    Task<CampaignSendWavePageDto?> GetSendWavePageAsync(Guid campaignId, Guid? teamId, CancellationToken ct = default);
    Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default);
    Task ImportCodesAsync(Guid campaignId, IEnumerable<string> codes, CancellationToken ct = default);
    Task ImportGeneratedCodesAsync(Guid campaignId, IReadOnlyList<string> codes, CancellationToken ct = default);
    Task ActivateAsync(Guid campaignId, CancellationToken ct = default);
    Task CompleteAsync(Guid campaignId, CancellationToken ct = default);
    Task<WaveSendPreview> PreviewWaveSendAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task<int> SendWaveAsync(Guid campaignId, Guid teamId, CancellationToken ct = default);
    Task ResendToGrantAsync(Guid grantId, CancellationToken ct = default);
    Task RetryAllFailedAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Returns campaign grants for a user where the campaign is Active or Completed,
    /// ordered by AssignedAt descending. Includes Campaign and Code navigations.
    /// </summary>
    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all campaign grants for a user (any campaign status),
    /// ordered by AssignedAt descending. Includes Campaign and Code navigations.
    /// Used for admin detail views.
    /// </summary>
    Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Marks campaign grants as redeemed based on discovered discount-code redemptions.
    /// Matches codes case-insensitively against active/completed campaigns' unredeemed grants.
    /// When a code matches grants in multiple campaigns, the most recently created campaign wins.
    /// Returns the number of grants marked as redeemed.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);
}

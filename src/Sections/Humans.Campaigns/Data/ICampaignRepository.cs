using Humans.Base.Interfaces.Repositories;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Campaigns.Data;

internal interface ICampaignRepository : IRepository
{
    /// <summary>With codes and grants. Read-only.</summary>
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Detached, without navigations, for <see cref="UpdateCampaignAsync"/>.</summary>
    Task<Campaign?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    /// <summary>Detached, with codes, for <see cref="UpdateCampaignAsync"/>.</summary>
    Task<Campaign?> FindForMutationWithCodesAsync(Guid id, CancellationToken ct = default);

    /// <summary>With codes and grants. Unordered.</summary>
    Task<List<Campaign>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Active and Completed campaigns, read apart from <see cref="GetCodeTrackingGrantRowsAsync"/> so a campaign with
    /// no grants still gets a row.
    /// </summary>
    Task<IReadOnlyList<CampaignCodeTrackingSummaryRow>> GetCodeTrackingSummariesAsync(
        CancellationToken ct = default);

    /// <summary>Every grant on an Active or Completed campaign. Rows carry the user id only.</summary>
    Task<IReadOnlyList<CampaignCodeTrackingGrantRow>> GetCodeTrackingGrantRowsAsync(
        CancellationToken ct = default);

    Task AddCampaignAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>
    /// Takes an entity from <see cref="FindForMutationAsync"/> or <see cref="FindForMutationWithCodesAsync"/>.
    /// </summary>
    Task UpdateCampaignAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>Atomic. No-op when the list is empty.</summary>
    Task AddCampaignCodesAsync(IReadOnlyList<CampaignCode> codes, CancellationToken ct = default);

    /// <summary>Not yet granted, in import order, up to <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<CampaignCode>> GetAvailableCodesAsync(
        Guid campaignId, int limit, CancellationToken ct = default);

    /// <summary>Not yet granted.</summary>
    Task<int> CountAvailableCodesAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>With campaign and code. Unordered. Read-only.</summary>
    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>With campaign and code. Read-only.</summary>
    Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Read-only; status changes go through <see cref="UpdateGrantStatusAsync"/>.</summary>
    Task<GrantWithSendContext?> GetGrantForResendAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>Read-only; status changes go through <see cref="UpdateGrantStatusAsync"/>.</summary>
    Task<IReadOnlyList<GrantWithSendContext>> GetFailedGrantsForRetryAsync(
        Guid campaignId, CancellationToken ct = default);

    /// <summary>Null when the grant does not exist.</summary>
    Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default);

    Task<HashSet<Guid>> GetAlreadyGrantedUserIdsAsync(
        Guid campaignId, CancellationToken ct = default);

    /// <summary>Commits immediately, one grant at a time.</summary>
    Task AddGrantAndSaveAsync(CampaignGrant grant, CancellationToken ct = default);

    /// <summary>Commits immediately, one grant at a time. False when the grant does not exist.</summary>
    Task<bool> UpdateGrantStatusAsync(
        Guid grantId,
        EmailOutboxStatus? status,
        Instant latestEmailAt,
        CancellationToken ct = default);

    /// <summary>
    /// One grant per redemption, so N orders with the same code redeem N grants. Active and Completed campaigns only;
    /// the newest campaign wins. Returns grants marked.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);

    Task<IReadOnlyList<GrantExportRow>> GetGrantsForUserExportAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> DeleteGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// On a same-campaign collision the target's grant wins and the source's is dropped. <paramref name="updatedAt"/>
    /// is ignored: grants have no <c>UpdatedAt</c>.
    /// </summary>
    Task ReassignGrantsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}

/// <summary>The recipient's address comes from <c>IUserEmailService</c>, not from here.</summary>
internal sealed record GrantWithSendContext(
    Guid GrantId,
    Guid UserId,
    Guid CampaignId,
    string CodeString,
    string CampaignEmailSubject,
    string CampaignEmailBodyTemplate,
    string? CampaignReplyToAddress);

/// <summary>Grant totals are aggregated in the service from <see cref="ICampaignRepository.GetCodeTrackingGrantRowsAsync"/>.</summary>
internal sealed record CampaignCodeTrackingSummaryRow(
    Guid CampaignId,
    string CampaignTitle,
    Instant CreatedAt);

internal sealed record CampaignCodeTrackingGrantRow(
    Guid CampaignId,
    string CampaignTitle,
    Guid GrantId,
    Guid UserId,
    string? Code,
    Instant? RedeemedAt,
    EmailOutboxStatus? LatestEmailStatus);

internal sealed record GrantExportRow(
    string CampaignTitle,
    string Code,
    Instant AssignedAt,
    Instant? RedeemedAt,
    EmailOutboxStatus? LatestEmailStatus);

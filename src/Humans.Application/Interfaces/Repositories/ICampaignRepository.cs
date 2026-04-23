using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Campaigns section's tables: <c>campaigns</c>,
/// <c>campaign_codes</c>, and <c>campaign_grants</c>. The only non-test file
/// that writes to these DbSets after the Campaigns migration lands.
/// </summary>
public interface ICampaignRepository
{
    // ==========================================================================
    // Campaigns
    // ==========================================================================

    /// <summary>
    /// Load a campaign with its codes and grants (codes + recipient-user FK)
    /// for read-only display. Returns a tracked entity so callers must treat
    /// it as read-only unless explicitly routed through a mutation method.
    /// </summary>
    Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Load a campaign for update via <see cref="UpdateCampaignAsync"/> —
    /// no navigation. Returns a detached entity.
    /// </summary>
    Task<Campaign?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Load a campaign for update with its codes loaded (used during
    /// activation and code import). Returns a detached entity.
    /// </summary>
    Task<Campaign?> FindForMutationWithCodesAsync(Guid id, CancellationToken ct = default);

    /// <summary>All campaigns ordered by CreatedAt descending, with codes and grants.</summary>
    Task<List<Campaign>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the summary header (id/title/creation order) for every
    /// Active or Completed campaign, ordered CreatedAt desc. Used together
    /// with <see cref="GetCodeTrackingGrantRowsAsync"/> to build the Tickets
    /// admin code-tracking dashboard (so campaigns with zero grants still
    /// surface a row).
    /// </summary>
    Task<IReadOnlyList<CampaignCodeTrackingSummaryRow>> GetCodeTrackingSummariesAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns one row per grant (campaign id, code string, redemption,
    /// email status, user id) for every grant on an Active or Completed
    /// campaign. Recipient display names are resolved by the caller via
    /// <c>IUserService.GetByIdsAsync</c>; grant rows carry only the user id.
    /// </summary>
    Task<IReadOnlyList<CampaignCodeTrackingGrantRow>> GetCodeTrackingGrantRowsAsync(
        CancellationToken ct = default);

    /// <summary>Persists a new campaign. Commits immediately.</summary>
    Task AddCampaignAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a mutated campaign (obtained via
    /// <see cref="FindForMutationAsync"/> or <see cref="FindForMutationWithCodesAsync"/>).
    /// Commits immediately.
    /// </summary>
    Task UpdateCampaignAsync(Campaign campaign, CancellationToken ct = default);

    // ==========================================================================
    // Campaign Codes
    // ==========================================================================

    /// <summary>
    /// Persists a batch of new campaign codes atomically. No-op when the list
    /// is empty.
    /// </summary>
    Task AddCampaignCodesAsync(IReadOnlyList<CampaignCode> codes, CancellationToken ct = default);

    /// <summary>
    /// Returns available codes (not yet granted) for a campaign, ordered by
    /// import order, up to <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<CampaignCode>> GetAvailableCodesAsync(
        Guid campaignId, int limit, CancellationToken ct = default);

    /// <summary>Counts available codes (not yet granted) for a campaign.</summary>
    Task<int> CountAvailableCodesAsync(Guid campaignId, CancellationToken ct = default);

    // ==========================================================================
    // Campaign Grants
    // ==========================================================================

    /// <summary>
    /// Returns active/completed campaign grants for a user, with campaign
    /// and code included, ordered AssignedAt desc. Read-only.
    /// </summary>
    Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every campaign grant for a user with campaign + code included.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single grant with its Campaign and Code (and campaign FK so
    /// the service can build an email request). Read-only — service calls
    /// <see cref="UpdateGrantStatusAsync"/> for status updates.
    /// </summary>
    Task<GrantWithSendContext?> GetGrantForResendAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>
    /// Returns failed grants for a campaign with their Campaign and Code.
    /// Read-only — service calls <see cref="UpdateGrantStatusAsync"/>.
    /// </summary>
    Task<IReadOnlyList<GrantWithSendContext>> GetFailedGrantsForRetryAsync(
        Guid campaignId, CancellationToken ct = default);

    /// <summary>Returns the campaign id for a given grant, or null if not found.</summary>
    Task<Guid?> GetCampaignIdForGrantAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>
    /// Returns the set of user IDs who have already received a grant in the
    /// given campaign.
    /// </summary>
    Task<HashSet<Guid>> GetAlreadyGrantedUserIdsAsync(
        Guid campaignId, CancellationToken ct = default);

    /// <summary>Stages a new grant and commits immediately (per-grant save pattern).</summary>
    Task AddGrantAndSaveAsync(CampaignGrant grant, CancellationToken ct = default);

    /// <summary>
    /// Updates a grant's email-status and last-email timestamp and commits
    /// immediately (per-grant save pattern during wave send / retry).
    /// Returns true if the grant was found and updated, false otherwise.
    /// </summary>
    Task<bool> UpdateGrantStatusAsync(
        Guid grantId,
        Humans.Domain.Enums.EmailOutboxStatus? status,
        Instant latestEmailAt,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the first unredeemed grant matching a given code string (across
    /// any Active/Completed campaign; most recent campaign wins). Returns the
    /// number of grants marked.
    /// </summary>
    Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default);

    /// <summary>Returns grants for a user's GDPR export.</summary>
    Task<IReadOnlyList<GrantExportRow>> GetGrantsForUserExportAsync(
        Guid userId, CancellationToken ct = default);

}

/// <summary>
/// A grant plus the minimum surface (campaign, code, user id) the service
/// needs to build a resend request without re-navigating cross-domain
/// nav properties. The service fetches the recipient user/email via
/// <c>IUserEmailService</c> composition.
/// </summary>
public record GrantWithSendContext(
    Guid GrantId,
    Guid CampaignId,
    Guid UserId,
    string CodeString,
    string CampaignTitle,
    string CampaignEmailSubject,
    string CampaignEmailBodyTemplate,
    string? CampaignReplyToAddress);

/// <summary>
/// Campaign header used by <see cref="ICampaignRepository.GetCodeTrackingSummariesAsync"/>.
/// Grant totals are aggregated in the service from
/// <see cref="ICampaignRepository.GetCodeTrackingGrantRowsAsync"/>.
/// </summary>
public record CampaignCodeTrackingSummaryRow(
    Guid CampaignId,
    string CampaignTitle,
    Instant CampaignCreatedAt);

/// <summary>
/// One grant per row, used by <see cref="ICampaignRepository.GetCodeTrackingGrantRowsAsync"/>.
/// The owning service stitches recipient display names via <c>IUserService</c>
/// so no cross-domain navigation is read at the repository layer.
/// </summary>
public record CampaignCodeTrackingGrantRow(
    Guid CampaignId,
    string CampaignTitle,
    Guid GrantId,
    Guid UserId,
    string? Code,
    Instant? RedeemedAt,
    Humans.Domain.Enums.EmailOutboxStatus? LatestEmailStatus);

/// <summary>Flat row for the GDPR grant export.</summary>
public record GrantExportRow(
    string CampaignTitle,
    string Code,
    Instant AssignedAt,
    Instant? RedeemedAt,
    Humans.Domain.Enums.EmailOutboxStatus? LatestEmailStatus);

using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ICampaignRepository"/>. The only
/// non-test file that touches <c>DbContext.Campaigns</c>,
/// <c>DbContext.CampaignCodes</c>, or <c>DbContext.CampaignGrants</c> after
/// the Campaigns migration lands.
/// </summary>
public sealed class CampaignRepository : ICampaignRepository
{
    private readonly HumansDbContext _dbContext;

    public CampaignRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ==========================================================================
    // Campaigns
    // ==========================================================================

    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Grants' User navigation is cross-domain and tagged obsolete in the
        // entity; we don't include it here. Consumers that need display names
        // for recipients resolve via IUserService keyed off CampaignGrant.UserId.
        return await _dbContext.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public Task<Campaign?> FindForMutationAsync(Guid id, CancellationToken ct = default)
    {
        return _dbContext.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public Task<Campaign?> FindForMutationWithCodesAsync(Guid id, CancellationToken ct = default)
    {
        return _dbContext.Campaigns
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<List<Campaign>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants)
            .AsSplitQuery()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public void AddCampaign(Campaign campaign) => _dbContext.Campaigns.Add(campaign);

    // ==========================================================================
    // Campaign Codes
    // ==========================================================================

    public void AddCampaignCode(CampaignCode code) => _dbContext.CampaignCodes.Add(code);

    public async Task<IReadOnlyList<CampaignCode>> GetAvailableCodesAsync(
        Guid campaignId, int limit, CancellationToken ct = default)
    {
        return await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                && !_dbContext.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .OrderBy(c => c.ImportOrder)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> CountAvailableCodesAsync(Guid campaignId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                && !_dbContext.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .CountAsync(ct);
    }

    // ==========================================================================
    // Campaign Grants
    // ==========================================================================

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == userId
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed))
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignGrant>> GetAllGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Include(g => g.Campaign)
            .Include(g => g.Code)
            .Where(g => g.UserId == userId)
            .OrderByDescending(g => g.AssignedAt)
            .ToListAsync(ct);
    }

    public async Task<GrantWithSendContext?> GetGrantForResendAsync(
        Guid grantId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => new GrantWithSendContext(
                g.Id,
                g.CampaignId,
                g.UserId,
                g.Code.Code,
                g.Campaign.Title,
                g.Campaign.EmailSubject,
                g.Campaign.EmailBodyTemplate,
                g.Campaign.ReplyToAddress))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<GrantWithSendContext>> GetFailedGrantsForRetryAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Where(g => g.CampaignId == campaignId
                && g.LatestEmailStatus == EmailOutboxStatus.Failed)
            .Select(g => new GrantWithSendContext(
                g.Id,
                g.CampaignId,
                g.UserId,
                g.Code.Code,
                g.Campaign.Title,
                g.Campaign.EmailSubject,
                g.Campaign.EmailBodyTemplate,
                g.Campaign.ReplyToAddress))
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetCampaignIdForGrantAsync(
        Guid grantId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => (Guid?)g.CampaignId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<HashSet<Guid>> GetAlreadyGrantedUserIdsAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        var list = await _dbContext.CampaignGrants
            .AsNoTracking()
            .Where(g => g.CampaignId == campaignId)
            .Select(g => g.UserId)
            .Distinct()
            .ToListAsync(ct);
        return list.ToHashSet();
    }

    public async Task AddGrantAndSaveAsync(CampaignGrant grant, CancellationToken ct = default)
    {
        _dbContext.CampaignGrants.Add(grant);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateGrantStatusAsync(
        Guid grantId,
        EmailOutboxStatus? status,
        Instant latestEmailAt,
        CancellationToken ct = default)
    {
        var grant = await _dbContext.CampaignGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null)
            return false;

        grant.LatestEmailStatus = status;
        grant.LatestEmailAt = latestEmailAt;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> MarkGrantsRedeemedAsync(
        IReadOnlyCollection<DiscountCodeRedemption> redemptions,
        CancellationToken ct = default)
    {
        if (redemptions.Count == 0)
            return 0;

        var codeStrings = redemptions
            .Where(r => !string.IsNullOrEmpty(r.Code))
            .Select(r => r.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (codeStrings.Count == 0)
            return 0;

        // Load unredeemed grants on active/completed campaigns. Filter by code
        // in memory so the DB query stays simple and collation-independent.
        var unredeemed = (await _dbContext.CampaignGrants
            .Include(g => g.Code)
            .Include(g => g.Campaign)
            .Where(g => g.Code != null
                && (g.Campaign.Status == CampaignStatus.Active || g.Campaign.Status == CampaignStatus.Completed)
                && g.RedeemedAt == null)
            .ToListAsync(ct))
            .Where(g => g.Code != null && codeStrings.Contains(g.Code.Code))
            .ToList();

        // Iterate redemptions in input order, matching one grant per redemption
        // so that N orders with the same code redeem N distinct grants. When a
        // code matches grants in multiple campaigns, the most recently created
        // campaign wins.
        var redeemedCount = 0;
        foreach (var redemption in redemptions)
        {
            if (string.IsNullOrEmpty(redemption.Code))
                continue;

            var grant = unredeemed
                .Where(g => string.Equals(g.Code!.Code, redemption.Code, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.Campaign.CreatedAt)
                .FirstOrDefault();

            if (grant is null)
                continue;

            grant.RedeemedAt = redemption.RedeemedAt;
            unredeemed.Remove(grant);
            redeemedCount++;
        }

        if (redeemedCount > 0)
            await _dbContext.SaveChangesAsync(ct);

        return redeemedCount;
    }

    public async Task<IReadOnlyList<GrantExportRow>> GetGrantsForUserExportAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.CampaignGrants
            .AsNoTracking()
            .Where(cg => cg.UserId == userId)
            .OrderByDescending(cg => cg.AssignedAt)
            .Select(cg => new GrantExportRow(
                cg.Campaign.Title,
                cg.Code.Code,
                cg.AssignedAt,
                cg.RedeemedAt,
                cg.LatestEmailStatus))
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Unit-of-work
    // ==========================================================================

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);
}

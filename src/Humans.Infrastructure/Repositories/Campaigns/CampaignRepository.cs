using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Application.Interfaces.Campaigns;

namespace Humans.Infrastructure.Repositories.Campaigns;

/// <summary>
/// EF-backed implementation of <see cref="ICampaignRepository"/>. The only
/// non-test file that touches <c>DbContext.Campaigns</c>,
/// <c>DbContext.CampaignCodes</c>, or <c>DbContext.CampaignGrants</c> after
/// the Campaigns migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class CampaignRepository : ICampaignRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CampaignRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Campaigns
    // ==========================================================================

    public async Task<Campaign?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Grants' User navigation is cross-domain and tagged obsolete in the
        // entity; we don't include it here. Consumers that need display names
        // for recipients resolve via IUserService keyed off CampaignGrant.UserId.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants).ThenInclude(g => g.Code)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Campaign?> FindForMutationAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Campaign?> FindForMutationWithCodesAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Campaigns
            .AsNoTracking()
            .Include(c => c.Codes)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<List<Campaign>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Campaigns
            .Include(c => c.Codes)
            .Include(c => c.Grants)
            .AsSplitQuery()
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignCodeTrackingSummaryRow>> GetCodeTrackingSummariesAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Campaigns
            .AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Active || c.Status == CampaignStatus.Completed)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CampaignCodeTrackingSummaryRow(c.Id, c.Title, c.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignCodeTrackingGrantRow>> GetCodeTrackingGrantRowsAsync(
        CancellationToken ct = default)
    {
        // Projected flat rows — no cross-domain .Include on CampaignGrant.User;
        // recipient display names are resolved by the service via IUserService.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
            .AsNoTracking()
            .Where(g => g.Campaign.Status == CampaignStatus.Active
                || g.Campaign.Status == CampaignStatus.Completed)
            .Select(g => new CampaignCodeTrackingGrantRow(
                g.CampaignId,
                g.Campaign.Title,
                g.Id,
                g.UserId,
                g.Code != null ? g.Code.Code : null,
                g.RedeemedAt,
                g.LatestEmailStatus))
            .ToListAsync(ct);
    }

    public async Task AddCampaignAsync(Campaign campaign, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Campaigns.Add(campaign);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateCampaignAsync(Campaign campaign, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(campaign);
        ctx.Entry(campaign).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Campaign Codes
    // ==========================================================================

    public async Task AddCampaignCodesAsync(IReadOnlyList<CampaignCode> codes, CancellationToken ct = default)
    {
        if (codes.Count == 0)
            return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampaignCodes.AddRange(codes);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CampaignCode>> GetAvailableCodesAsync(
        Guid campaignId, int limit, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                && !ctx.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .OrderBy(c => c.ImportOrder)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> CountAvailableCodesAsync(Guid campaignId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignCodes
            .Where(c => c.CampaignId == campaignId
                && !ctx.CampaignGrants.Any(g => g.CampaignCodeId == c.Id))
            .CountAsync(ct);
    }

    // ==========================================================================
    // Campaign Grants
    // ==========================================================================

    public async Task<IReadOnlyList<CampaignGrant>> GetActiveOrCompletedGrantsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
            .AsNoTracking()
            .Where(g => g.Id == grantId)
            .Select(g => (Guid?)g.CampaignId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<HashSet<Guid>> GetAlreadyGrantedUserIdsAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var list = await ctx.CampaignGrants
            .AsNoTracking()
            .Where(g => g.CampaignId == campaignId)
            .Select(g => g.UserId)
            .Distinct()
            .ToListAsync(ct);
        return list.ToHashSet();
    }

    public async Task AddGrantAndSaveAsync(CampaignGrant grant, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampaignGrants.Add(grant);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateGrantStatusAsync(
        Guid grantId,
        EmailOutboxStatus? status,
        Instant latestEmailAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var grant = await ctx.CampaignGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null)
            return false;

        grant.LatestEmailStatus = status;
        grant.LatestEmailAt = latestEmailAt;
        await ctx.SaveChangesAsync(ct);
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

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Load unredeemed grants on active/completed campaigns. Filter by code
        // in memory so the DB query stays simple and collation-independent.
        var unredeemed = (await ctx.CampaignGrants
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
            await ctx.SaveChangesAsync(ct);

        return redeemedCount;
    }

    public async Task<IReadOnlyList<GrantExportRow>> GetGrantsForUserExportAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampaignGrants
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
}

using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ICampRepository"/>. The only
/// non-test file that touches the Camp-owned DbSets
/// (<c>Camps</c>, <c>CampSeasons</c>, <c>CampLeads</c>, <c>CampImages</c>,
/// <c>CampHistoricalNames</c>, <c>CampSettings</c>) after the Camps migration
/// lands. Uses <see cref="IDbContextFactory{TContext}"/> so the repository can
/// be registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// Cross-domain navigation (<c>CampLead.User</c>) is never <c>Include</c>-ed;
/// the service stitches display data via <see cref="Humans.Application.Interfaces.IUserService"/>.
/// </summary>
public sealed class CampRepository : ICampRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CampRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads — Camp
    // ==========================================================================

    public async Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
            .Include(b => b.Leads.Where(l => l.LeftAt == null))
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Slug == normalizedSlug, ct);
    }

    public async Task<Camp?> GetByIdAsync(Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
            .Include(b => b.Leads.Where(l => l.LeftAt == null))
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Id == campId, ct);
    }

    public async Task<IReadOnlyList<Camp>> GetPublicCampsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons.Where(s => s.Year == year &&
                (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full)))
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .Include(b => b.HistoricalNames)
            .Where(b => b.Seasons.Any(s => s.Year == year &&
                (s.Status == CampSeasonStatus.Active || s.Status == CampSeasonStatus.Full)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Camp>> GetAllCampsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons.Where(s => s.Year == year))
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .Include(b => b.HistoricalNames)
            .Where(b => b.Seasons.Any(s => s.Year == year))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Camp>> GetCampsWithLeadsForYearAsync(
        int year,
        IReadOnlyList<CampSeasonStatus>? statusFilter,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.Camps
            .AsNoTracking()
            .Include(c => c.Seasons.Where(s => s.Year == year))
            .Include(c => c.Leads.Where(l => l.LeftAt == null))
            .Where(c => c.Seasons.Any(s => s.Year == year));

        if (statusFilter is { Count: > 0 })
        {
            query = query.Where(c => c.Seasons.Any(s => s.Year == year && statusFilter.Contains(s.Status)));
        }

        return await query
            .OrderBy(c => c.Seasons.Where(s => s.Year == year).Select(s => s.Name).FirstOrDefault())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Camp>> GetCampsByLeadUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .Include(b => b.Leads)
            .Where(b => b.Leads.Any(l => l.UserId == userId && l.LeftAt == null))
            .ToListAsync(ct);
    }

    public async Task<int> CountPendingSeasonsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .CountAsync(s => s.Status == CampSeasonStatus.Pending, ct);
    }

    public async Task<IReadOnlyList<CampSeason>> GetPendingSeasonsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .ThenInclude(c => c.Leads.Where(l => l.LeftAt == null))
            .Where(s => s.Status == CampSeasonStatus.Pending)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps.AnyAsync(b => b.Slug == slug, ct);
    }

    // ==========================================================================
    // Writes — Camp (aggregate)
    // ==========================================================================

    public async Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampLead creatorLead,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Camps.Add(camp);
        ctx.CampSeasons.Add(initialSeason);
        ctx.CampLeads.Add(creatorLead);
        if (historicalNames is { Count: > 0 })
        {
            foreach (var name in historicalNames)
            {
                ctx.CampHistoricalNames.Add(name);
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateCampFieldsAsync(
        Guid campId,
        string contactEmail,
        string contactPhone,
        string? webOrSocialUrl,
        IReadOnlyList<CampLink>? links,
        bool isSwissCamp,
        int timesAtNowhere,
        bool hideHistoricalNames,
        Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var camp = await ctx.Camps.FindAsync([campId], ct);
        if (camp is null)
        {
            return false;
        }

        camp.ContactEmail = contactEmail;
        camp.ContactPhone = contactPhone;
        camp.WebOrSocialUrl = webOrSocialUrl;
        camp.Links = links?.ToList();
        if (camp.Links is { Count: > 0 })
        {
            camp.WebOrSocialUrl = null;
        }

        camp.IsSwissCamp = isSwissCamp;
        camp.HideHistoricalNames = hideHistoricalNames;
        camp.TimesAtNowhere = timesAtNowhere;
        camp.UpdatedAt = updatedAt;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<int>> GetCampYearsAsync(
        Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .Select(s => s.Year)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>?> DeleteCampAsync(
        Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var camp = await ctx.Camps.FindAsync([campId], ct);
        if (camp is null)
        {
            return null;
        }

        var images = await ctx.CampImages
            .Where(i => i.CampId == campId)
            .Select(i => i.StoragePath)
            .ToListAsync(ct);

        ctx.Camps.Remove(camp);
        await ctx.SaveChangesAsync(ct);
        return images;
    }

    // ==========================================================================
    // Writes — Season
    // ==========================================================================

    public async Task<CampSeason?> GetSeasonForMutationAsync(
        Guid seasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var season = await ctx.CampSeasons.FindAsync([seasonId], ct);
        if (season is null)
        {
            return null;
        }

        ctx.Entry(season).State = EntityState.Detached;
        return season;
    }

    public async Task UpdateSeasonAsync(CampSeason season, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampSeasons.Update(season);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> SeasonExistsAsync(
        Guid campId, int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .AnyAsync(s => s.CampId == campId && s.Year == year, ct);
    }

    public async Task<CampSeason?> GetLatestSeasonAsync(
        Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> HasApprovedSeasonAsync(
        Guid campId, CancellationToken ct = default)
    {
        var approvedStatuses = new[]
        {
            CampSeasonStatus.Active, CampSeasonStatus.Full, CampSeasonStatus.Withdrawn
        };
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .AnyAsync(s => s.CampId == campId && approvedStatuses.Contains(s.Status), ct);
    }

    public async Task AddSeasonAsync(CampSeason season, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampSeasons.Add(season);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SetNameLockDateForYearAsync(
        int year, LocalDate lockDate, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var seasons = await ctx.CampSeasons
            .Where(s => s.Year == year)
            .ToListAsync(ct);

        foreach (var season in seasons)
        {
            season.NameLockDate = lockDate;
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<int, LocalDate?>> GetNameLockDatesAsync(
        IReadOnlyCollection<int> years,
        CancellationToken ct = default)
    {
        if (years.Count == 0)
        {
            return new Dictionary<int, LocalDate?>();
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => years.Contains(s.Year))
            .GroupBy(s => s.Year)
            .Select(g => new { Year = g.Key, LockDate = g.Max(s => s.NameLockDate) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Year, r => r.LockDate);
    }

    // ==========================================================================
    // Cross-service queries
    // ==========================================================================

    public async Task<SoundZone?> GetSeasonSoundZoneAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.SoundZone)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> GetSeasonNameAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<(Guid CampSeasonId, Guid CampId, int Year)?> GetSeasonInfoAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.Id == campSeasonId)
            .Select(s => new { s.Id, s.CampId, s.Year })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : (row.Id, row.CampId, row.Year);
    }

    public async Task<IReadOnlyDictionary<Guid, (string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement)>>
        GetSeasonDisplayDataForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .Where(s => s.Year == year)
            .Select(s => new { s.Id, s.Name, s.Camp.Slug, s.SoundZone, s.SpaceRequirement })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => (r.Name, r.Slug, r.SoundZone, r.SpaceRequirement));
    }

    public async Task<IReadOnlyList<(Guid Id, string Name, string CampSlug, SpaceSize? SpaceRequirement)>>
        GetSeasonBriefsForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .Where(s => s.Year == year)
            .Select(s => new { s.Id, s.Name, CampSlug = s.Camp.Slug, s.SpaceRequirement })
            .ToListAsync(ct);

        return rows.Select(r => (r.Id, r.Name, r.CampSlug, r.SpaceRequirement)).ToList();
    }

    public async Task<Guid?> GetCampLeadSeasonIdForYearAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.LeftAt == null)
            .Join(ctx.CampSeasons,
                l => l.CampId,
                s => s.CampId,
                (l, s) => s)
            .Where(s => s.Year == year)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    // ==========================================================================
    // Leads
    // ==========================================================================

    public async Task<bool> IsUserActiveLeadAsync(
        Guid userId, Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .AnyAsync(l => l.CampId == campId && l.UserId == userId && l.LeftAt == null, ct);
    }

    public async Task<int> CountActiveLeadsAsync(Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .CountAsync(l => l.CampId == campId && l.LeftAt == null, ct);
    }

    public async Task AddLeadAsync(CampLead lead, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampLeads.Add(lead);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<CampLead?> GetLeadForMutationAsync(
        Guid leadId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var lead = await ctx.CampLeads.FindAsync([leadId], ct);
        if (lead is null)
        {
            return null;
        }

        ctx.Entry(lead).State = EntityState.Detached;
        return lead;
    }

    public async Task UpdateLeadAsync(CampLead lead, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampLeads.Update(lead);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .Where(l => l.LeftAt == null)
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .AnyAsync(l => l.UserId == userId && l.LeftAt == null, ct);
    }

    public async Task<IReadOnlyList<CampLead>> GetAllLeadAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .Include(cl => cl.Camp)
            .Where(cl => cl.UserId == userId)
            .OrderByDescending(cl => cl.JoinedAt)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Historical names
    // ==========================================================================

    public async Task AddHistoricalNameAsync(
        CampHistoricalName historicalName, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampHistoricalNames.Add(historicalName);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveHistoricalNameAsync(
        Guid historicalNameId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var entry = await ctx.CampHistoricalNames.FindAsync([historicalNameId], ct);
        if (entry is null)
        {
            return false;
        }

        ctx.CampHistoricalNames.Remove(entry);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Images
    // ==========================================================================

    public async Task<int> CountImagesAsync(Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampImages
            .AsNoTracking()
            .CountAsync(i => i.CampId == campId, ct);
    }

    public async Task AddImageAsync(CampImage image, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampImages.Add(image);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<CampImage?> GetImageForMutationAsync(
        Guid imageId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var image = await ctx.CampImages.FindAsync([imageId], ct);
        if (image is null)
        {
            return null;
        }

        ctx.Entry(image).State = EntityState.Detached;
        return image;
    }

    public async Task<(string StoragePath, Guid CampId)?> DeleteImageAsync(
        Guid imageId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var image = await ctx.CampImages.FindAsync([imageId], ct);
        if (image is null)
        {
            return null;
        }

        var result = (image.StoragePath, image.CampId);
        ctx.CampImages.Remove(image);
        await ctx.SaveChangesAsync(ct);
        return result;
    }

    public async Task ReorderImagesAsync(
        Guid campId,
        IReadOnlyList<Guid> imageIdsInOrder,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var images = await ctx.CampImages
            .Where(i => i.CampId == campId)
            .ToListAsync(ct);

        for (var i = 0; i < imageIdsInOrder.Count; i++)
        {
            var image = images.FirstOrDefault(img => img.Id == imageIdsInOrder[i]);
            if (image is not null)
            {
                image.SortOrder = i;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    // ==========================================================================
    // Settings
    // ==========================================================================

    public async Task<CampSettings?> GetSettingsReadOnlyAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSettings
            .AsNoTracking()
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetPublicYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CampSettings.OrderBy(s => s.Id).FirstAsync(ct);
        settings.PublicYear = year;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> OpenSeasonAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CampSettings.OrderBy(s => s.Id).FirstAsync(ct);
        if (settings.OpenSeasons.Contains(year))
        {
            return false;
        }

        settings.OpenSeasons.Add(year);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CloseSeasonAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CampSettings.OrderBy(s => s.Id).FirstAsync(ct);
        if (!settings.OpenSeasons.Remove(year))
        {
            return false;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }
}

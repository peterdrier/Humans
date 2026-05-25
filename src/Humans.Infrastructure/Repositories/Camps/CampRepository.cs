using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Camps;

/// <summary>EF-backed <see cref="ICampRepository"/>.</summary>
internal sealed class CampRepository : ICampRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CampRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // Reads — Camp

    public async Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Slug == normalizedSlug, ct);
    }

    public async Task<Camp?> GetByIdAsync(Guid campId, CancellationToken ct = default)
    {
        // Active Seasons.Members included so RefreshEntryAsync sees the same shape
        // as the warmup path — otherwise EeGrantedCount projects as 0 on each invalidation.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
                .ThenInclude(s => s.Members.Where(m => m.Status == CampMemberStatus.Active))
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(b => b.Id == campId, ct);
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
                .ThenInclude(s => s.Members.Where(m => m.Status == CampMemberStatus.Active))
            .Where(c => c.Seasons.Any(s => s.Year == year));

        if (statusFilter is { Count: > 0 })
        {
            query = query.Where(c => c.Seasons.Any(s => s.Year == year && statusFilter.Contains(s.Status)));
        }

        return await query
            .OrderBy(c => c.Seasons.Where(s => s.Year == year).Select(s => s.Name).FirstOrDefault())
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
            .Where(s => s.Status == CampSeasonStatus.Pending)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps.AnyAsync(b => b.Slug == slug, ct);
    }

    // Contains() → IN clause (enum stored as string; see no-enum-compare-in-ef).
    private static readonly CampSeasonStatus[] PublicCampSeasonStatuses = [CampSeasonStatus.Active, CampSeasonStatus.Full
    ];

    public async Task<IReadOnlyList<Camp>> SearchForYearAsync(
        string query, int year, bool onlyPublicStatus, int max,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        var pattern = "%" + EscapeLikePattern(query.Trim()) + "%";

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var baseQuery = ctx.Camps
            .AsNoTracking()
            .Include(c => c.Seasons.Where(s => s.Year == year));

        // Name-match and public-status MUST bind to the same season row —
        // splitting across two .Where(.Any(...)) lets different seasons satisfy each.
        IQueryable<Camp> q = onlyPublicStatus
            ? baseQuery.Where(c => c.Seasons.Any(s => s.Year == year
                && EF.Functions.ILike(s.Name, pattern, "\\")
                && PublicCampSeasonStatuses.Contains(s.Status)))
            : baseQuery.Where(c => c.Seasons.Any(s => s.Year == year
                && EF.Functions.ILike(s.Name, pattern, "\\")));

        return await q
            .OrderBy(c => c.Slug) // arch:db-sort-ok — orchestrator re-ranks by score
            .Take(max)
            .ToListAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    // Writes — Camp (aggregate)

    public async Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampMember creatorMember,
        CampRoleAssignment? creatorLeadAssignment,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Camps.Add(camp);
        ctx.CampSeasons.Add(initialSeason);
        ctx.CampMembers.Add(creatorMember);
        if (creatorLeadAssignment is not null)
        {
            ctx.CampRoleAssignments.Add(creatorLeadAssignment);
        }
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

    // Writes — Season

    public async Task<bool> UpdateSeasonAsync(
        Guid seasonId,
        Action<CampSeason> mutate,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var season = await ctx.CampSeasons.FindAsync([seasonId], ct);
        if (season is null)
        {
            return false;
        }

        mutate(season);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ApplyNameChangeAsync(
        Guid seasonId,
        Func<CampSeason, CampHistoricalName?> mutate,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var season = await ctx.CampSeasons.FindAsync([seasonId], ct);
        if (season is null)
        {
            return false;
        }

        var historyEntry = mutate(season);
        if (historyEntry is not null)
        {
            ctx.CampHistoricalNames.Add(historyEntry);
        }

        await ctx.SaveChangesAsync(ct);
        return true;
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

    // Cross-service queries

    public async Task<CampSeason?> GetSeasonByIdAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, (string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement, Guid CampId)>>
        GetSeasonDisplayDataForYearAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .Where(s => s.Year == year)
            .Select(s => new { s.Id, s.Name, s.Camp.Slug, s.SoundZone, s.SpaceRequirement, s.CampId })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => (r.Name, r.Slug, r.SoundZone, r.SpaceRequirement, r.CampId));
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

    // Leads (legacy camp_leads — only the seed-migration snapshot + role-backed
    // team-sync reads remain; mutation/query methods retired with the Camp Lead
    // role move. Entity/table kept until nobodies-collective/Humans#774.)

    public async Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(
        CancellationToken ct = default)
    {
        // Post-Camp-Lead-retirement source of truth (issue
        // nobodies-collective/Humans#753): CampRoleAssignment against the
        // Camp Lead special role. Existing semantic preserved — no year filter
        // (Barrio Leads team is year-agnostic). Note: cross-aggregate read of
        // camp_role_definitions/camp_role_assignments; both tables live in the
        // Camps section so this stays within the section boundary.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .Where(a => a.Definition.SpecialRole == CampSpecialRole.Lead
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken ct = default)
    {
        // Same post-retirement repoint as GetActiveLeadUserIdsAsync — see comment there.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == CampSpecialRole.Lead
                && a.Definition.DeactivatedAt == null, ct);
    }

    public async Task<IReadOnlyList<CampLead>> GetAllLeadAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        // GDPR export of legacy camp_leads rows until #774 drops the table.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampLeads
            .AsNoTracking()
            .Include(cl => cl.Camp)
            .Where(cl => cl.UserId == userId)
            .OrderByDescending(cl => cl.JoinedAt) // arch:db-sort-ok — GDPR export ordering
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LeadMigrationSnapshot>> GetLeadMigrationSnapshotsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampLeads
            .AsNoTracking()
            .Where(l => l.LeftAt == null)
            .Select(l => new { l.Id, l.CampId, l.UserId, CampSlug = l.Camp.Slug })
            .ToListAsync(ct);
        return rows
            .Select(r => new LeadMigrationSnapshot(r.Id, r.CampId, r.UserId, r.CampSlug))
            .ToList();
    }

    public async Task<Guid?> GetCampSeasonForLeadMigrationAsync(
        Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Prefer the open (Pending/Active/Full) season with the latest year;
        // fall back to the most-recent season of any status. Returns null when
        // the camp has no seasons at all.
        var openLatest = await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId
                && (s.Status == CampSeasonStatus.Pending
                    || s.Status == CampSeasonStatus.Active
                    || s.Status == CampSeasonStatus.Full))
            .OrderByDescending(s => s.Year) // arch:db-sort-ok — picking single most-relevant season for one-shot lead migration
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        if (openLatest is not null) return openLatest;

        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year) // arch:db-sort-ok — picking single most-relevant season for one-shot lead migration
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    // Historical names

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

    // Images

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

    // Settings

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

    public async Task SetEeStartDateAsync(
        LocalDate? eeStartDate, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var settings = await ctx.CampSettings.FirstAsync(cancellationToken);
        settings.EeStartDate = eeStartDate;
        await ctx.SaveChangesAsync(cancellationToken);
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

    // Membership (camp_members)

    public async Task<CampMemberInsertResult> RequestMembershipAsync(
        Guid campSeasonId, Guid userId, Instant requestedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var existing = await ctx.CampMembers
            .AsNoTracking()
            .Where(m => m.CampSeasonId == campSeasonId && m.UserId == userId && m.Status != CampMemberStatus.Removed)
            .Select(m => new { m.Id, m.Status })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new CampMemberInsertResult(
                existing.Id,
                existing.Status == CampMemberStatus.Active
                    ? CampMemberInsertOutcome.AlreadyActive
                    : CampMemberInsertOutcome.AlreadyPending);
        }

        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = userId,
            Status = CampMemberStatus.Pending,
            RequestedAt = requestedAt
        };
        ctx.CampMembers.Add(member);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return new CampMemberInsertResult(member.Id, CampMemberInsertOutcome.Created);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Race: a concurrent request won the insert. Resolve to the winner.
            var winner = await ctx.CampMembers
                .AsNoTracking()
                .Where(m => m.CampSeasonId == campSeasonId && m.UserId == userId && m.Status != CampMemberStatus.Removed)
                .Select(m => new { m.Id, m.Status })
                .FirstOrDefaultAsync(ct);
            if (winner is null)
            {
                throw;
            }
            return new CampMemberInsertResult(
                winner.Id,
                winner.Status == CampMemberStatus.Active
                    ? CampMemberInsertOutcome.AlreadyActive
                    : CampMemberInsertOutcome.AlreadyPending);
        }
    }

    public async Task<CampMemberInsertResult> AddActiveMembershipAsync(
        Guid campSeasonId, Guid userId, Instant now, Guid confirmedByUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Only consider non-Removed rows — the partial unique index permits
        // multiple Removed rows alongside one non-Removed row per (season, user).
        var existing = await ctx.CampMembers.FirstOrDefaultAsync(
            m => m.CampSeasonId == campSeasonId
                 && m.UserId == userId
                 && m.Status != CampMemberStatus.Removed,
            ct);

        if (existing is not null)
        {
            if (existing.Status == CampMemberStatus.Active)
                return new CampMemberInsertResult(existing.Id, CampMemberInsertOutcome.AlreadyActive);

            existing.Status = CampMemberStatus.Active;
            existing.ConfirmedAt = now;
            existing.ConfirmedByUserId = confirmedByUserId;
            await ctx.SaveChangesAsync(ct);
            return new CampMemberInsertResult(existing.Id, CampMemberInsertOutcome.Created);
        }

        var newMember = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = now,
            ConfirmedAt = now,
            ConfirmedByUserId = confirmedByUserId,
        };
        ctx.CampMembers.Add(newMember);
        await ctx.SaveChangesAsync(ct);
        return new CampMemberInsertResult(newMember.Id, CampMemberInsertOutcome.Created);
    }

    public async Task<CampMember?> GetMemberForCampMutationAsync(
        Guid campMemberId, Guid scopedCampId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var member = await ctx.CampMembers
            .Include(m => m.CampSeason)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == campMemberId, ct);
        if (member is null || member.CampSeason.CampId != scopedCampId)
        {
            return null;
        }
        return member;
    }

    public async Task<CampMember?> GetMemberForOwnMutationAsync(
        Guid campMemberId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var member = await ctx.CampMembers
            .Include(m => m.CampSeason)
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == campMemberId, ct);
        if (member is null || member.UserId != userId)
        {
            return null;
        }
        return member;
    }

    public async Task SaveMemberAsync(CampMember member, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.CampMembers.Attach(member);
        ctx.Entry(member).State = EntityState.Modified;
        // Protect immutable init-only fields.
        ctx.Entry(member).Property(m => m.CampSeasonId).IsModified = false;
        ctx.Entry(member).Property(m => m.UserId).IsModified = false;
        ctx.Entry(member).Property(m => m.RequestedAt).IsModified = false;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<CampMember?> GetUserMembershipInSeasonAsync(
        Guid campSeasonId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.CampSeasonId == campSeasonId
                    && m.UserId == userId
                    && m.Status != CampMemberStatus.Removed,
                ct);
    }

    public async Task<IReadOnlyList<CampMember>> GetSeasonMembersAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampMembers
            .AsNoTracking()
            .Where(m => m.CampSeasonId == campSeasonId && m.Status != CampMemberStatus.Removed)
            .OrderBy(m => m.RequestedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampMember>>> GetMembersForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampMembers
            .AsNoTracking()
            .Where(m => m.CampSeason.Year == year && m.Status != CampMemberStatus.Removed)
            .ToListAsync(ct);
        return rows
            .GroupBy(m => m.CampSeasonId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CampMember>)g.ToList());
    }

    public async Task<IReadOnlyList<CampMember>> GetUserMembershipsAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampMembers
            .AsNoTracking()
            .Include(m => m.CampSeason)
                .ThenInclude(s => s.Camp)
            .Where(m => m.UserId == userId && m.Status != CampMemberStatus.Removed)
            .OrderByDescending(m => m.CampSeason.Year)
            .ThenBy(m => m.CampSeason.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingRequesterUserIdsForSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampMembers
            .AsNoTracking()
            .Where(m => m.CampSeasonId == campSeasonId && m.Status == CampMemberStatus.Pending)
            .Select(m => m.UserId)
            .ToListAsync(ct);
    }

    public async Task<(Guid CampSeasonId, Guid UserId, CampMemberStatus Status)?> GetMemberLookupAsync(
        Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.CampMembers.AsNoTracking()
            .Where(m => m.Id == campMemberId)
            .Select(m => new { m.CampSeasonId, m.UserId, m.Status })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.CampSeasonId, row.UserId, row.Status);
    }

    // Early Entry

    public async Task<int> GetGrantedCountForSeasonAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.CampMembers
            .CountAsync(m => m.CampSeasonId == campSeasonId
                          && m.HasEarlyEntry
                          && m.Status == CampMemberStatus.Active,
                        cancellationToken);
    }

    public async Task<(int OldValue, int NewValue, Guid CampId)?> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        var season = await ctx.CampSeasons
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
        if (season is null) return null;

        var oldValue = season.EeSlotCount;
        if (oldValue == slotCount) return (oldValue, slotCount, season.CampId);

        season.EeSlotCount = slotCount;
        await ctx.SaveChangesAsync(cancellationToken);
        return (oldValue, slotCount, season.CampId);
    }
}

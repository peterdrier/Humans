using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Camps.Data;

internal sealed partial class CampRepository : ICampRepository
{
    private readonly IDbContextFactory<CampsDbContext> _factory;

    public CampRepository(IDbContextFactory<CampsDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
                .ThenInclude(s => s.Members.Where(m => m.Status != CampMemberStatus.Removed))
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images)
            .FirstOrDefaultAsync(b => b.Slug == normalizedSlug, ct);
    }

    public async Task<Camp?> GetByIdAsync(Guid campId, CancellationToken ct = default)
    {
        // Seasons.Members included so the CachingCampService invalidation rebuild sees the same shape as warmup — otherwise EeGrantedCount projects as 0.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps
            .AsNoTracking()
            .Include(b => b.Seasons)
                .ThenInclude(s => s.Members.Where(m => m.Status != CampMemberStatus.Removed))
            .Include(b => b.HistoricalNames)
            .Include(b => b.Images)
            .FirstOrDefaultAsync(b => b.Id == campId, ct);
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
                .ThenInclude(s => s.Members.Where(m => m.Status != CampMemberStatus.Removed))
            .Include(c => c.Images)
            .Include(c => c.HistoricalNames)
            .Where(c => c.Seasons.Any(s => s.Year == year));

        if (statusFilter is { Count: > 0 })
        {
            query = query.Where(c => c.Seasons.Any(s => s.Year == year && statusFilter.Contains(s.Status)));
        }

        return await query.ToListAsync(ct);
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Camps.AnyAsync(b => b.Slug == slug, ct);
    }

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
        Guid campId, Guid eventSettingsId, int? unassignedYear, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .AnyAsync(s => s.CampId == campId
                && (s.EventSettingsId == eventSettingsId
                    || (s.EventSettingsId == null && s.Year == unassignedYear)), ct);
    }

    public async Task<CampSeason?> GetLatestSeasonAsync(
        Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year) // arch:db-sort-ok latest-season selector by identity (FirstOrDefault)
            .ThenByDescending(s => s.CreatedAt)
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

    public async Task<int> SetNameLockDateForEventAsync(
        Guid eventSettingsId, int? unassignedYear, LocalDate lockDate, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var seasons = await ctx.CampSeasons
            .Where(s => s.EventSettingsId == eventSettingsId
                || (s.EventSettingsId == null && s.Year == unassignedYear))
            .ToListAsync(ct);

        var changed = seasons.Where(s => s.NameLockDate != lockDate).ToList();
        foreach (var season in changed)
        {
            season.NameLockDate = lockDate;
        }

        if (changed.Count > 0)
            await ctx.SaveChangesAsync(ct);
        return changed.Count;
    }

    public async Task<IReadOnlyList<(int Year, Guid? EventSettingsId, LocalDate? LockDate)>> GetNameLockDatesAsync(
        IReadOnlyCollection<int> years,
        CancellationToken ct = default)
    {
        if (years.Count == 0)
        {
            return [];
        }

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => years.Contains(s.Year))
            .GroupBy(s => new { s.Year, s.EventSettingsId })
            .Select(g => new { g.Key.Year, g.Key.EventSettingsId, LockDate = g.Max(s => s.NameLockDate) })
            .ToListAsync(ct);

        return [.. rows.Select(r => (r.Year, r.EventSettingsId, r.LockDate))];
    }

    public async Task<CampSeason?> GetSeasonByIdAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(
        CancellationToken ct = default)
    {
        // No year filter: the Barrio Leads team is year-agnostic.
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == CampSpecialRole.Lead
                && a.Definition.DeactivatedAt == null, ct);
    }

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
        var sortOrders = await ctx.CampImages
            .Where(i => i.CampId == image.CampId)
            .Select(i => i.SortOrder)
            .ToListAsync(ct);
        image.SortOrder = sortOrders.DefaultIfEmpty(-1).Max() + 1;
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

    public async Task<CampSettings?> GetSettingsReadOnlyAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSettings
            .AsNoTracking()
            .OrderBy(s => s.Id) // arch:db-sort-ok deterministic camp-settings selector by identity
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CampEventSettings>> GetEventSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampEventSettings.AsNoTracking().ToListAsync(ct);
    }

    public async Task<bool> SetRegistrationOpenAsync(Guid eventSettingsId, bool open, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var row = await ctx.CampEventSettings.FindAsync([eventSettingsId], ct);
        if (row is null)
        {
            ctx.CampEventSettings.Add(new CampEventSettings { EventSettingsId = eventSettingsId, RegistrationOpen = open });
        }
        else if (row.RegistrationOpen == open)
        {
            return false;
        }
        else
        {
            row.RegistrationOpen = open;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> AddMissingEventSettingsAsync(
        IReadOnlyDictionary<Guid, bool> registrationOpenByEventId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.CampEventSettings.Select(r => r.EventSettingsId).ToListAsync(ct);
        var added = registrationOpenByEventId.Keys.Except(existing)
            .Select(id => new CampEventSettings { EventSettingsId = id, RegistrationOpen = registrationOpenByEventId[id] })
            .ToList();
        ctx.CampEventSettings.AddRange(added);
        await ctx.SaveChangesAsync(ct);
        return added.Count;
    }

    public async Task<bool> ClearLegacyOpenSeasonsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CampSettings.OrderBy(s => s.Id).FirstAsync(ct); // arch:db-sort-ok deterministic camp-settings selector by identity
        if (settings.OpenSeasons.Count == 0)
            return false;

        settings.OpenSeasons = [];
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CampSeason>> GetSeasonsWithoutEventIdAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasons.AsNoTracking().Where(s => s.EventSettingsId == null).ToListAsync(ct);
    }

    public async Task<int> AssignSeasonEventIdsAsync(
        IReadOnlyDictionary<Guid, Guid> eventSettingsIdBySeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var ids = eventSettingsIdBySeasonId.Keys.ToList();
        var rows = await ctx.CampSeasons.Where(s => ids.Contains(s.Id) && s.EventSettingsId == null).ToListAsync(ct);
        foreach (var row in rows)
            row.EventSettingsId = eventSettingsIdBySeasonId[row.Id];
        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

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

    public async Task<IReadOnlyList<CampMember>> GetAllMembershipsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampMembers.AsNoTracking()
            .Include(m => m.CampSeason).ThenInclude(s => s.Camp)
            .Where(m => m.UserId == userId)
            .ToListAsync(ct);
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

    public async Task<int> GetGrantedCountForSeasonAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        // No status filter: HasEarlyEntry survives on Removed rows when the
        // holder had already entered the event (consumed slot) — counting them
        // keeps remove-and-regrant from yielding extra early entries.
        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.CampMembers
            .CountAsync(m => m.CampSeasonId == campSeasonId && m.HasEarlyEntry,
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

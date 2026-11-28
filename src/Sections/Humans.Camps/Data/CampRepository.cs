using Humans.Base.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using System.Transactions;

namespace Humans.Camps.Data;

internal sealed class CampRepository(IDbContextFactory<CampsDbContext> factory)
    : SectionRepository<CampsDbContext>(factory), ICampRepository
{
    public async Task<Camp?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.Camps.AnyAsync(b => b.Slug == slug, ct), ct);

    public async Task CreateCampAsync(
        Camp camp,
        CampSeason initialSeason,
        CampMember creatorMember,
        CampRoleAssignment? creatorLeadAssignment,
        IReadOnlyList<CampHistoricalName>? historicalNames,
        CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        Guid campId, Guid eventSettingsId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampSeasons
            .AsNoTracking()
            .AnyAsync(s => s.CampId == campId
                && s.EventSettingsId == eventSettingsId, ct), ct);

    public async Task<CampSeason?> GetLatestSeasonAsync(
        Guid campId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampSeasons
            .AsNoTracking()
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year) // arch:db-sort-ok latest-season selector by identity (FirstOrDefault)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct), ct);

    public async Task<bool> HasApprovedSeasonAsync(
        Guid campId, CancellationToken ct = default)
    {
        var approvedStatuses = new[]
        {
            CampSeasonStatus.Active, CampSeasonStatus.Full, CampSeasonStatus.Withdrawn
        };
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.CampSeasons
            .AsNoTracking()
            .AnyAsync(s => s.CampId == campId && approvedStatuses.Contains(s.Status), ct);
    }

    public async Task AddSeasonAsync(CampSeason season, CancellationToken ct = default) =>
        await InsertAsync(season, ct);

    public async Task<int> SetNameLockDateForEventAsync(
        Guid eventSettingsId, LocalDate lockDate, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var seasons = await ctx.CampSeasons
            .Where(s => s.EventSettingsId == eventSettingsId)
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

        await using var ctx = await CreateDbContextAsync(ct);
        var rows = await ctx.CampSeasons
            .AsNoTracking()
            .Where(s => years.Contains(s.Year))
            .GroupBy(s => new { s.Year, s.EventSettingsId })
            .Select(g => new { g.Key.Year, g.Key.EventSettingsId, LockDate = g.Max(s => s.NameLockDate) })
            .ToListAsync(ct);

        return [.. rows.Select(r => (r.Year, r.EventSettingsId, r.LockDate))];
    }

    public async Task<CampSeason?> GetSeasonByIdAsync(
        Guid campSeasonId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampSeasons
            .AsNoTracking()
            .Include(s => s.Camp)
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, ct), ct);

    public async Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(
        CancellationToken ct = default)
    {
        // No year filter: the Barrio Leads team is year-agnostic.
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.CampRoleAssignments
            .AsNoTracking()
            .Where(a => a.Definition.SpecialRole == CampSpecialRole.Lead
                && a.Definition.DeactivatedAt == null)
            .Select(a => a.CampMember.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments
            .AsNoTracking()
            .AnyAsync(a => a.CampMember.UserId == userId
                && a.Definition.SpecialRole == CampSpecialRole.Lead
                && a.Definition.DeactivatedAt == null, ct), ct);

    public async Task AddHistoricalNameAsync(
        CampHistoricalName historicalName, CancellationToken ct = default) =>
        await InsertAsync(historicalName, ct);

    public async Task<bool> RemoveHistoricalNameAsync(
        Guid historicalNameId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var entry = await ctx.CampHistoricalNames.FindAsync([historicalNameId], ct);
        if (entry is null)
        {
            return false;
        }

        ctx.CampHistoricalNames.Remove(entry);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CountImagesAsync(Guid campId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampImages
            .AsNoTracking()
            .CountAsync(i => i.CampId == campId, ct), ct);

    public async Task AddImageAsync(CampImage image, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<CampSettings?> GetSettingsReadOnlyAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampSettings
            .AsNoTracking()
            .OrderBy(s => s.Id) // arch:db-sort-ok deterministic camp-settings selector by identity
            .FirstOrDefaultAsync(ct), ct);

    public async Task<IReadOnlyList<CampEventSettings>> GetEventSettingsAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampEventSettings.AsNoTracking().ToListAsync(ct), ct);

    public async Task<bool> SetRegistrationOpenAsync(Guid eventSettingsId, bool open, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<bool> SetRegistrationInfoAsync(Guid eventSettingsId, string? registrationInfo, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var row = await ctx.CampEventSettings.FindAsync([eventSettingsId], ct);
        if (row is null)
        {
            ctx.CampEventSettings.Add(new CampEventSettings { EventSettingsId = eventSettingsId, RegistrationInfo = registrationInfo });
        }
        else if (string.Equals(row.RegistrationInfo, registrationInfo, StringComparison.Ordinal))
        {
            return false;
        }
        else
        {
            row.RegistrationInfo = registrationInfo;
        }

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<CampMemberInsertResult> RequestMembershipAsync(
        Guid campSeasonId, Guid userId, Instant requestedAt, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

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
        await using var ctx = await CreateDbContextAsync(ct);
        var result = await PrepareActiveMembershipAsync(ctx, campSeasonId, userId, now, confirmedByUserId, ct);
        if (result.Outcome != CampMemberInsertOutcome.AlreadyActive)
            await ctx.SaveChangesAsync(ct);
        return result;
    }

    private static async Task<CampMemberInsertResult> PrepareActiveMembershipAsync(
        CampsDbContext ctx, Guid campSeasonId, Guid userId, Instant now, Guid confirmedByUserId, CancellationToken ct)
    {
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
        return new CampMemberInsertResult(newMember.Id, CampMemberInsertOutcome.Created);
    }

    public async Task<CampMember?> GetMemberForCampMutationAsync(
        Guid campMemberId, Guid scopedCampId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(ct);
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

    public async Task<IReadOnlyList<CampMember>> GetAllMembershipsForUserAsync(Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampMembers.AsNoTracking()
            .Include(m => m.CampSeason).ThenInclude(s => s.Camp)
            .Where(m => m.UserId == userId)
            .ToListAsync(ct), ct);

    public async Task<int> SaveMemberAsync(CampMember member, CancellationToken ct = default, bool removeRoleAssignments = false)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        ctx.CampMembers.Attach(member);
        ctx.Entry(member).State = EntityState.Modified;
        // Protect immutable init-only fields.
        ctx.Entry(member).Property(m => m.CampSeasonId).IsModified = false;
        ctx.Entry(member).Property(m => m.UserId).IsModified = false;
        ctx.Entry(member).Property(m => m.RequestedAt).IsModified = false;
        var assignments = removeRoleAssignments
            ? await ctx.CampRoleAssignments.Where(a => a.CampMemberId == member.Id).ToListAsync(ct)
            : [];
        ctx.CampRoleAssignments.RemoveRange(assignments);
        await ctx.SaveChangesAsync(ct);
        return assignments.Count;
    }

    public async Task<IReadOnlyList<Guid>> GetPendingRequesterIdsAsync(
        Guid campSeasonId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampMembers
            .AsNoTracking()
            .Where(m => m.CampSeasonId == campSeasonId && m.Status == CampMemberStatus.Pending)
            .Select(m => m.UserId)
            .ToListAsync(ct), ct);

    public async Task<(Guid CampSeasonId, Guid UserId, CampMemberStatus Status)?> GetMemberLookupAsync(
        Guid campMemberId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
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
        await using var ctx = await CreateDbContextAsync(cancellationToken);
        return await ctx.CampMembers
            .CountAsync(m => m.CampSeasonId == campSeasonId && m.HasEarlyEntry,
                        cancellationToken);
    }

    public async Task<(int OldValue, int NewValue, Guid CampId)?> SetCampSeasonEeSlotCountAsync(
        Guid campSeasonId, int slotCount, CancellationToken cancellationToken = default)
    {
        await using var ctx = await CreateDbContextAsync(cancellationToken);
        var season = await ctx.CampSeasons
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
        if (season is null) return null;

        var oldValue = season.EeSlotCount;
        if (oldValue == slotCount) return (oldValue, slotCount, season.CampId);

        season.EeSlotCount = slotCount;
        await ctx.SaveChangesAsync(cancellationToken);
        return (oldValue, slotCount, season.CampId);
    }

    public async Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var query = ctx.CampRoleDefinitions.AsNoTracking().AsQueryable();
        if (!includeDeactivated)
            query = query.Where(d => d.DeactivatedAt == null);
        return await query.ToListAsync(ct);
    }

    public async Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct), ct);

    public async Task<CampRoleDefinition?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var definitions = await ctx.CampRoleDefinitions.AsNoTracking().ToListAsync(ct);
        return definitions.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CampRoleDefinition?> GetSpecialDefinitionAsync(CampSpecialRole specialRole, CancellationToken ct = default)
    {
        if (specialRole == CampSpecialRole.None)
            throw new ArgumentException("CampSpecialRole.None has no special definition.", nameof(specialRole));
        await using var ctx = await CreateDbContextAsync(ct);
        return await ctx.CampRoleDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.SpecialRole == specialRole, ct);
    }

    public async Task<IReadOnlyList<CampSpecialRole>> GetExistingSpecialRolesAsync(CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleDefinitions.AsNoTracking()
            .Where(d => d.SpecialRole != CampSpecialRole.None)
            .Select(d => d.SpecialRole)
            .Distinct()
            .ToListAsync(ct), ct);

    public async Task<bool> DefinitionSlugExistsAsync(string slug, Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var query = ctx.CampRoleDefinitions.AsNoTracking();
        if (excludingId is { } id)
            query = query.Where(d => d.Id != id);
        var slugs = await query.Select(d => d.Slug).ToListAsync(ct);
        return slugs.Exists(s => string.Equals(s, slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> DefinitionNameExistsAsync(string name, Guid? excludingId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var query = ctx.CampRoleDefinitions.AsNoTracking();
        if (excludingId is { } id)
            query = query.Where(d => d.Id != id);
        var names = await query.Select(d => d.Name).ToListAsync(ct);
        return names.Exists(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddDefinitionAsync(CampRoleDefinition definition, CancellationToken ct = default) =>
        await InsertAsync(definition, ct);

    public async Task<bool> UpdateDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var def = await ctx.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (def is null) return false;
        mutate(def);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Where(a => a.CampSeasonId == campSeasonId)
            .ToListAsync(ct), ct);

    public async Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct), ct);

    public async Task<int> CountRoleAssignmentsAsync(Guid campSeasonId, Guid definitionId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments.AsNoTracking()
            .CountAsync(a => a.CampSeasonId == campSeasonId && a.CampRoleDefinitionId == definitionId, ct), ct);

    public async Task<bool> AssignmentExistsAsync(Guid campSeasonId, Guid definitionId, Guid campMemberId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments.AsNoTracking()
            .AnyAsync(a => a.CampSeasonId == campSeasonId
                        && a.CampRoleDefinitionId == definitionId
                        && a.CampMemberId == campMemberId, ct), ct);

    public async Task<(bool Assigned, CampMemberInsertResult? Membership)> AddAssignmentAsync(
        CampRoleAssignment assignment, CancellationToken ct = default, Guid? addUserId = null)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        CampMemberInsertResult? membership = null;
        if (addUserId is { } userId)
        {
            membership = await PrepareActiveMembershipAsync(
                ctx, assignment.CampSeasonId, userId, assignment.AssignedAt, assignment.AssignedByUserId, ct);
            assignment = new CampRoleAssignment
            {
                Id = assignment.Id,
                CampSeasonId = assignment.CampSeasonId,
                CampRoleDefinitionId = assignment.CampRoleDefinitionId,
                CampMemberId = membership.MemberId,
                AssignedAt = assignment.AssignedAt,
                AssignedByUserId = assignment.AssignedByUserId,
            };
        }
        ctx.CampRoleAssignments.Add(assignment);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return (true, membership);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_camp_role_assignments_unique" })
        {
            // Unique-index race on (CampSeasonId, CampRoleDefinitionId, CampMemberId) —
            // caller treats false as "already holds role".
            return (false, null);
        }
    }

    public async Task<bool> DeleteAssignmentAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var assignment = await ctx.CampRoleAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment is null) return false;
        ctx.CampRoleAssignments.Remove(assignment);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAllAssignmentsForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await RunAsync(ctx => ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.Definition)
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason).ThenInclude(s => s.Camp)
            .Where(a => a.CampMember.UserId == userId)
            .ToListAsync(ct), ct);

    public async Task<int> DeleteCampFootprintForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);

        var members = await ctx.CampMembers
            .Where(m => m.UserId == userId)
            .ToListAsync(ct);
        if (members.Count == 0) return 0;

        var memberIds = members.Select(m => m.Id).ToList();
        var assignments = await ctx.CampRoleAssignments
            .Where(a => memberIds.Contains(a.CampMemberId))
            .ToListAsync(ct);

        ctx.CampRoleAssignments.RemoveRange(assignments);
        ctx.CampMembers.RemoveRange(members);
        await ctx.SaveChangesAsync(ct);
        return assignments.Count + members.Count;
    }

    public async Task<IReadOnlyList<(Guid CampSeasonId, Guid DefinitionId, int Count)>> GetAssignmentCountsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        var rows = await ctx.CampRoleAssignments.AsNoTracking()
            .Where(a => a.CampSeason.Year == year)
            .GroupBy(a => new { a.CampSeasonId, a.CampRoleDefinitionId })
            .Select(g => new { g.Key.CampSeasonId, g.Key.CampRoleDefinitionId, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => (r.CampSeasonId, r.CampRoleDefinitionId, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForDefinitionInYearAsync(
        Guid definitionId, int year, CancellationToken ct = default)
    {
        await using var ctx = await CreateDbContextAsync(ct);
        // Display sort happens in the service (CampRoleService.BuildDrillDownAsync).
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .Where(a => a.CampRoleDefinitionId == definitionId && a.CampSeason.Year == year)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampRoleAssignment>> GetActiveAssignmentsForYearsAsync(
        IReadOnlyCollection<int> years, CancellationToken ct = default)
    {
        if (years.Count == 0) return [];
        await using var ctx = await CreateDbContextAsync(ct);
        var yearList = years.Distinct().ToList();
        return await ctx.CampRoleAssignments.AsNoTracking()
            .Include(a => a.CampMember)
            .Include(a => a.CampSeason)
            .Include(a => a.Definition)
            .Where(a => yearList.Contains(a.CampSeason.Year)
                     && a.Definition.DeactivatedAt == null
                     && a.CampMember.Status == CampMemberStatus.Active)
            .ToListAsync(ct);
    }

    public async Task ReassignMembershipsToUserAsync(
        Guid sourceUserId, Guid targetUserId,
        CancellationToken ct = default)
    {
        using var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);
        await using var ctx = await CreateDbContextAsync(ct);

        // Tracked — these rows are re-pointed (UserId) or deleted below.
        var sourceMembers = await ctx.CampMembers
            .Where(m => m.UserId == sourceUserId)
            .ToListAsync(ct);
        if (sourceMembers.Count == 0)
        {
            scope.Complete();
            return;
        }

        var sourceSeasonIds = sourceMembers.Select(m => m.CampSeasonId).Distinct().ToList();

        // Survivor's live (non-Removed) member per affected season — tracked, so a
        // colliding fold can reconcile its state below. A live source member in one of
        // these seasons would collide on IX_camp_members_active_unique, so it is
        // folded-then-dropped rather than re-pointed; removed source members never
        // collide (the partial index excludes them) and always re-point. At most one
        // live member per season, so the dictionary key is unique.
        var targetLiveMemberBySeason = (await ctx.CampMembers
                .Where(m => m.UserId == targetUserId
                    && m.Status != CampMemberStatus.Removed
                    && sourceSeasonIds.Contains(m.CampSeasonId))
                .ToListAsync(ct))
            .ToDictionary(m => m.CampSeasonId);

        // source CampMemberId -> survivor CampMemberId, for colliding seasons only.
        var collidingMemberToTarget = new Dictionary<Guid, Guid>();
        foreach (var sm in sourceMembers)
        {
            var isLive = sm.Status != CampMemberStatus.Removed;
            if (isLive && targetLiveMemberBySeason.TryGetValue(sm.CampSeasonId, out var tm))
            {
                // Both accounts hold a live membership this season; the rows collapse
                // onto the survivor's. Reconcile so the survivor keeps the best of both
                // before the source row is dropped: Active beats Pending (a more
                // authoritative source must not be downgraded — active-assignment
                // queries filter Status == Active), and an inherited early-entry grant
                // is OR-ed in rather than lost.
                if (sm.Status == CampMemberStatus.Active && tm.Status != CampMemberStatus.Active)
                {
                    tm.Status = CampMemberStatus.Active;
                    tm.ConfirmedAt ??= sm.ConfirmedAt;
                    tm.ConfirmedByUserId ??= sm.ConfirmedByUserId;
                }
                tm.HasEarlyEntry |= sm.HasEarlyEntry;
                collidingMemberToTarget[sm.Id] = tm.Id;
            }
            else
            {
                // Re-point the membership; its CampRoleAssignment rows ride along on the
                // unchanged CampMemberId. UserId is init-only -> mutate via change-tracker.
                ctx.Entry(sm).Property(nameof(CampMember.UserId)).CurrentValue = targetUserId;
            }
        }

        // Fold the colliding source members' role assignments onto the survivor's member,
        // deduping against IX_camp_role_assignments_unique
        // (CampSeasonId, CampRoleDefinitionId, CampMemberId), before those members go.
        if (collidingMemberToTarget.Count > 0)
        {
            var collidingMemberIds = collidingMemberToTarget.Keys.ToList();
            var targetMemberIds = collidingMemberToTarget.Values.Distinct().ToList();

            var existingKeys = new HashSet<(Guid, Guid, Guid)>(
                (await ctx.CampRoleAssignments.AsNoTracking()
                    .Where(a => targetMemberIds.Contains(a.CampMemberId))
                    .Select(a => new { a.CampSeasonId, a.CampRoleDefinitionId, a.CampMemberId })
                    .ToListAsync(ct))
                .Select(t => (t.CampSeasonId, t.CampRoleDefinitionId, t.CampMemberId)));

            var sourceAssignments = await ctx.CampRoleAssignments
                .Where(a => collidingMemberIds.Contains(a.CampMemberId))
                .ToListAsync(ct);

            foreach (var src in sourceAssignments)
            {
                var targetMemberId = collidingMemberToTarget[src.CampMemberId];
                var key = (src.CampSeasonId, src.CampRoleDefinitionId, targetMemberId);
                if (existingKeys.Contains(key))
                {
                    // Survivor already holds this role for the season — drop source's duplicate.
                    ctx.CampRoleAssignments.Remove(src);
                }
                else
                {
                    ctx.Entry(src).Property(nameof(CampRoleAssignment.CampMemberId))
                        .CurrentValue = targetMemberId;
                    existingKeys.Add(key);
                }
            }
        }

        // Save role moves/removals BEFORE deleting colliding members, so the cascade cannot delete moved roles.
        // Both saves share the transaction: failed member removal must roll back the role and membership fold.
        await ctx.SaveChangesAsync(ct);

        if (collidingMemberToTarget.Count > 0)
        {
            ctx.CampMembers.RemoveRange(
                sourceMembers.Where(m => collidingMemberToTarget.ContainsKey(m.Id)));
            await ctx.SaveChangesAsync(ct);
        }
        scope.Complete();
    }
}

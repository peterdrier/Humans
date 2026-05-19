using Humans.Application.DTOs.Events;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Events;

internal sealed class EventRepository(IDbContextFactory<HumansDbContext> factory) : IEventRepository
{
    // ── Settings ─────────────────────────────────────────────────────────

    public async Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventGuideSettings.AsNoTracking().FirstOrDefaultAsync(ct);
    }

    public async Task UpsertGuideSettingsAsync(EventGuideSettings settings, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventGuideSettings.FirstOrDefaultAsync(ct);
        if (existing == null)
        {
            ctx.EventGuideSettings.Add(settings);
        }
        else
        {
            existing.EventSettingsId = settings.EventSettingsId;
            existing.SubmissionOpenAt = settings.SubmissionOpenAt;
            existing.SubmissionCloseAt = settings.SubmissionCloseAt;
            existing.GuidePublishAt = settings.GuidePublishAt;
            existing.MaxPrintSlots = settings.MaxPrintSlots;
            existing.UpdatedAt = settings.UpdatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    // ── Categories ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventCategories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventCategories
            .AsNoTracking()
            .Include(c => c.Events)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.EventCategories.AsNoTracking().Where(c => c.Slug == slug);
        if (excludeId.HasValue) query = query.Where(c => c.Id != excludeId.Value);
        return await query.AnyAsync(ct);
    }

    public async Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventCategories.AsNoTracking().Select(c => (int?)c.DisplayOrder).MaxAsync(ct) ?? 0;
    }

    public async Task AddCategoryAsync(EventCategory category, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.EventCategories.Add(category);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveCategoryAsync(EventCategory category, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(category);
        ctx.Entry(category).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var category = await ctx.EventCategories
            .Include(c => c.Events)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category == null) return (false, -1);
        if (category.Events.Count > 0) return (false, category.Events.Count);

        ctx.EventCategories.Remove(category);
        await ctx.SaveChangesAsync(ct);
        return (true, 0);
    }

    public async Task SwapCategoryOrderAsync(Guid id, int direction, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var categories = await ctx.EventCategories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);
        var index = categories.FindIndex(c => c.Id == id);
        if (index < 0) return;
        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= categories.Count) return;
        (categories[index].DisplayOrder, categories[targetIndex].DisplayOrder) =
            (categories[targetIndex].DisplayOrder, categories[index].DisplayOrder);
        await ctx.SaveChangesAsync(ct);
    }

    // ── Venues ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventVenues
            .AsNoTracking()
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventVenues
            .AsNoTracking()
            .Include(v => v.Events)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(ct);
    }

    public async Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventVenues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventVenues.AsNoTracking().Select(v => (int?)v.DisplayOrder).MaxAsync(ct) ?? 0;
    }

    public async Task AddVenueAsync(EventVenue venue, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.EventVenues.Add(venue);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveVenueAsync(EventVenue venue, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(venue);
        ctx.Entry(venue).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var venue = await ctx.EventVenues
            .Include(v => v.Events)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (venue == null) return (false, -1);
        if (venue.Events.Count > 0) return (false, venue.Events.Count);

        ctx.EventVenues.Remove(venue);
        await ctx.SaveChangesAsync(ct);
        return (true, 0);
    }

    public async Task SwapVenueOrderAsync(Guid id, int direction, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var venues = await ctx.EventVenues
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(ct);
        var index = venues.FindIndex(v => v.Id == id);
        if (index < 0) return;
        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= venues.Count) return;
        (venues[index].DisplayOrder, venues[targetIndex].DisplayOrder) =
            (venues[targetIndex].DisplayOrder, venues[index].DisplayOrder);
        await ctx.SaveChangesAsync(ct);
    }

    // ── Events (submitter) ────────────────────────────────────────────────

    public async Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EventVenue)
            .Where(e => e.CampId == null && e.SubmitterUserId == userId)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync(ct);
    }

    public async Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events.AsNoTracking().FirstOrDefaultAsync(
            e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == userId, ct);
    }

    public async Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .Where(e => e.CampId == campId)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync(ct);
    }

    public async Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events.AsNoTracking().FirstOrDefaultAsync(
            e => e.Id == eventId && e.CampId == campId, ct);
    }

    public async Task AddEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Events.Add(guideEvent);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task SaveEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(guideEvent);
        ctx.Entry(guideEvent).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    // ── Events (browse / export / API) ────────────────────────────────────

    public async Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EventVenue)
            .Where(e => e.Status == EventStatus.Approved);

        if (excludedSlugs.Count > 0)
            query = query.Where(e => !excludedSlugs.Contains(e.Category.Slug));
        if (categoryId.HasValue)
            query = query.Where(e => e.CategoryId == categoryId.Value);
        if (venueId.HasValue)
            query = query.Where(e => e.GuideSharedVenueId == venueId.Value);
        if (campId.HasValue)
            query = query.Where(e => e.CampId == campId.Value);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => EF.Functions.ILike(e.Title, $"%{q}%") ||
                                     EF.Functions.ILike(e.Description, $"%{q}%"));

        return await query.OrderBy(e => e.StartAt).ToListAsync(ct);
    }

    public async Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EventVenue)
            .FirstOrDefaultAsync(e => e.Id == id && e.Status == EventStatus.Approved, ct);
    }

    public async Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .ToListAsync(ct);
    }

    // ── Events (moderation) ───────────────────────────────────────────────

    public async Task<Dictionary<EventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var moderationStatuses = new[]
        {
            EventStatus.Pending,
            EventStatus.Approved,
            EventStatus.Rejected,
            EventStatus.ResubmitRequested,
            EventStatus.Withdrawn,
        };

        var groups = await ctx.Events
            .AsNoTracking()
            .Where(e => moderationStatuses.Contains(e.Status))
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return groups.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.Events
            .AsNoTracking()
            .Include(e => e.Category)
            .Include(e => e.EventVenue)
            .Include(e => e.EventModerationActions)
            .Where(e => e.Status == status);

        query = status == EventStatus.Pending
            ? query.OrderBy(e => e.SubmittedAt)
            : query.OrderByDescending(e => e.SubmittedAt);

        return await query.ToListAsync(ct);
    }

    public async Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
    }

    public async Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.Events
            .AsNoTracking()
            .Where(e => e.CampId != null &&
                        (e.Status == EventStatus.Pending || e.Status == EventStatus.Approved))
            .Select(e => new { e.Id, e.CampId, e.Title, e.StartAt, e.DurationMinutes, e.Status })
            .ToListAsync(ct);

        return rows.Select(r => new CampEventOverlap(r.Id, r.CampId, r.Title, r.StartAt, r.DurationMinutes, r.Status))
                   .ToList();
    }

    public async Task SaveEventAndModerationActionAsync(Event guideEvent, EventModerationAction action, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(guideEvent);
        ctx.Entry(guideEvent).State = EntityState.Modified;
        ctx.EventModerationActions.Add(action);
        await ctx.SaveChangesAsync(ct);
    }

    // ── Favourites ────────────────────────────────────────────────────────

    public async Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var ids = await ctx.EventFavourites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => f.GuideEventId)
            .ToListAsync(ct);
        return [.. ids];
    }

    public async Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventFavourites
            .AsNoTracking()
            .Include(f => f.Event).ThenInclude(e => e.Category)
            .Include(f => f.Event).ThenInclude(e => e.EventVenue)
            .Where(f => f.UserId == userId && f.Event.Status == EventStatus.Approved)
            .OrderBy(f => f.Event.StartAt)
            .ToListAsync(ct);
    }

    public async Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventFavourites.AsNoTracking()
            .AnyAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);
    }

    public async Task<bool> ToggleFavouriteAsync(Guid userId, Guid eventId, EventFavourite newFavourite, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventFavourites
            .FirstOrDefaultAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);
        if (existing != null)
        {
            ctx.EventFavourites.Remove(existing);
            await ctx.SaveChangesAsync(ct);
            return false;
        }

        ctx.EventFavourites.Add(newFavourite);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddFavouriteIfAbsentAsync(EventFavourite favourite, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var exists = await ctx.EventFavourites
            .AnyAsync(f => f.UserId == favourite.UserId && f.GuideEventId == favourite.GuideEventId, ct);
        if (exists) return false;
        ctx.EventFavourites.Add(favourite);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventFavourites
            .FirstOrDefaultAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);
        if (existing == null) return false;
        ctx.EventFavourites.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ── Preferences ───────────────────────────────────────────────────────

    public async Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task UpsertPreferenceAsync(Guid userId, string excludedCategorySlugsJson, Instant updatedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (existing == null)
        {
            ctx.EventPreferences.Add(new EventPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ExcludedCategorySlugs = excludedCategorySlugsJson,
                UpdatedAt = updatedAt
            });
        }
        else
        {
            existing.ExcludedCategorySlugs = excludedCategorySlugsJson;
            existing.UpdatedAt = updatedAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    // ── GDPR contributor ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventFavourite>> GetFavouritesForContributorAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventFavourites
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .ToListAsync(ct);
    }
}

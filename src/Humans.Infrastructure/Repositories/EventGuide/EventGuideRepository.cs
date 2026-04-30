using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.EventGuide;

public sealed class EventGuideRepository : IEventGuideRepository
{
    private readonly HumansDbContext _db;

    public EventGuideRepository(HumansDbContext db) => _db = db;

    // ── Settings ─────────────────────────────────────────────────────────

    public Task<GuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
        => _db.GuideSettings.Include(g => g.EventSettings).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EventSettings>> GetActiveEventSettingsAsync(CancellationToken ct = default)
        => await _db.EventSettings
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.GateOpeningDate)
            .ToListAsync(ct);

    public Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => _db.EventSettings.FindAsync([id], ct).AsTask();

    public void Add(GuideSettings settings) => _db.GuideSettings.Add(settings);

    // ── Categories ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
        => await _db.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
        => await _db.EventCategories
            .Include(c => c.GuideEvents)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

    public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
        => _db.EventCategories.FindAsync([id], ct).AsTask();

    public Task<EventCategory?> GetCategoryWithEventsAsync(Guid id, CancellationToken ct = default)
        => _db.EventCategories.Include(c => c.GuideEvents).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default)
    {
        var query = _db.EventCategories.Where(c => c.Slug == slug);
        if (excludeId.HasValue) query = query.Where(c => c.Id != excludeId.Value);
        return query.AnyAsync(ct);
    }

    public async Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default)
        => await _db.EventCategories.Select(c => (int?)c.DisplayOrder).MaxAsync(ct) ?? 0;

    public Task<List<EventCategory>> GetAllCategoriesOrderedForSwapAsync(CancellationToken ct = default)
        => _db.EventCategories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ToListAsync(ct);

    public void Add(EventCategory category) => _db.EventCategories.Add(category);
    public void Remove(EventCategory category) => _db.EventCategories.Remove(category);

    // ── Venues ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<GuideSharedVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
        => await _db.GuideSharedVenues
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<GuideSharedVenue>> GetAllVenuesAsync(CancellationToken ct = default)
        => await _db.GuideSharedVenues
            .Include(v => v.GuideEvents)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(ct);

    public Task<GuideSharedVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
        => _db.GuideSharedVenues.FindAsync([id], ct).AsTask();

    public Task<GuideSharedVenue?> GetVenueWithEventsAsync(Guid id, CancellationToken ct = default)
        => _db.GuideSharedVenues.Include(v => v.GuideEvents).FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default)
        => await _db.GuideSharedVenues.Select(v => (int?)v.DisplayOrder).MaxAsync(ct) ?? 0;

    public Task<List<GuideSharedVenue>> GetAllVenuesOrderedForSwapAsync(CancellationToken ct = default)
        => _db.GuideSharedVenues.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Name).ToListAsync(ct);

    public void Add(GuideSharedVenue venue) => _db.GuideSharedVenues.Add(venue);
    public void Remove(GuideSharedVenue venue) => _db.GuideSharedVenues.Remove(venue);

    // ── Events (submitter) ────────────────────────────────────────────────

    public async Task<IReadOnlyList<GuideEvent>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
        => await _db.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.GuideSharedVenue)
            .Where(e => e.CampId == null && e.SubmitterUserId == userId)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync(ct);

    public Task<GuideEvent?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => _db.GuideEvents.FirstOrDefaultAsync(
            e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == userId, ct);

    public void Add(GuideEvent guideEvent) => _db.GuideEvents.Add(guideEvent);

    // ── Events (browse / export / API) ────────────────────────────────────

    public async Task<IReadOnlyList<GuideEvent>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        var query = _db.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == GuideEventStatus.Approved);

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

    public Task<GuideEvent?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
        => _db.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .FirstOrDefaultAsync(e => e.Id == id && e.Status == GuideEventStatus.Approved, ct);

    public async Task<IReadOnlyList<GuideEvent>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
        => await _db.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .ToListAsync(ct);

    // ── Events (moderation) ───────────────────────────────────────────────

    public async Task<Dictionary<GuideEventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default)
    {
        var groups = await _db.GuideEvents
            .Where(e => e.Status == GuideEventStatus.Pending
                     || e.Status == GuideEventStatus.Approved
                     || e.Status == GuideEventStatus.Rejected
                     || e.Status == GuideEventStatus.ResubmitRequested)
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return groups.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<IReadOnlyList<GuideEvent>> GetEventsByStatusAsync(GuideEventStatus status, CancellationToken ct = default)
    {
        var query = _db.GuideEvents
            .Include(e => e.Category)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.GuideSharedVenue)
            .Include(e => e.ModerationActions).ThenInclude(a => a.ActorUser).ThenInclude(u => u.Profile)
            .Where(e => e.Status == status);

        query = status == GuideEventStatus.Pending
            ? query.OrderBy(e => e.SubmittedAt)
            : query.OrderByDescending(e => e.SubmittedAt);

        return await query.ToListAsync(ct);
    }

    public Task<GuideEvent?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => _db.GuideEvents
            .Include(e => e.SubmitterUser).ThenInclude(u => u.Profile)
            .Include(e => e.Camp)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    public async Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default)
    {
        var rows = await _db.GuideEvents
            .Where(e => e.CampId != null &&
                        (e.Status == GuideEventStatus.Pending || e.Status == GuideEventStatus.Approved))
            .Select(e => new { e.Id, e.CampId, e.Title, e.StartAt, e.DurationMinutes, e.Status })
            .ToListAsync(ct);

        return rows.Select(r => new CampEventOverlap(r.Id, r.CampId, r.Title, r.StartAt, r.DurationMinutes, r.Status))
                   .ToList();
    }

    public void Add(ModerationAction action) => _db.ModerationActions.Add(action);

    // ── Favourites ────────────────────────────────────────────────────────

    public async Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await _db.UserEventFavourites
            .Where(f => f.UserId == userId)
            .Select(f => f.GuideEventId)
            .ToListAsync(ct);
        return [.. ids];
    }

    public async Task<IReadOnlyList<UserEventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
        => await _db.UserEventFavourites
            .Include(f => f.GuideEvent).ThenInclude(e => e.Category)
            .Include(f => f.GuideEvent).ThenInclude(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(f => f.GuideEvent).ThenInclude(e => e.GuideSharedVenue)
            .Where(f => f.UserId == userId && f.GuideEvent.Status == GuideEventStatus.Approved)
            .OrderBy(f => f.GuideEvent.StartAt)
            .ToListAsync(ct);

    public Task<UserEventFavourite?> GetFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _db.UserEventFavourites.FirstOrDefaultAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);

    public Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _db.UserEventFavourites.AnyAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);

    public void Add(UserEventFavourite fav) => _db.UserEventFavourites.Add(fav);
    public void Remove(UserEventFavourite fav) => _db.UserEventFavourites.Remove(fav);

    // ── Preferences ───────────────────────────────────────────────────────

    public Task<UserGuidePreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
        => _db.UserGuidePreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public void Add(UserGuidePreference pref) => _db.UserGuidePreferences.Add(pref);

    // ── Persistence ───────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

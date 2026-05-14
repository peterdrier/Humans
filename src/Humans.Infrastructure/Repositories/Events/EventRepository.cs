using Humans.Application.DTOs.Events;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Events;

public sealed class EventRepository : IEventRepository
{
    private readonly HumansDbContext _db;

    public EventRepository(HumansDbContext db) => _db = db;

    // ── Settings ─────────────────────────────────────────────────────────

    public Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
        => _db.EventGuideSettings.Include(g => g.EventSettings).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<EventSettings>> GetActiveEventSettingsAsync(CancellationToken ct = default)
        => await _db.EventSettings
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.GateOpeningDate)
            .ToListAsync(ct);

    public Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => _db.EventSettings.FindAsync([id], ct).AsTask();

    public void Add(EventGuideSettings settings) => _db.EventGuideSettings.Add(settings);

    // ── Categories ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
        => await _db.EventCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.DisplayOrder)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
        => await _db.EventCategories
            .Include(c => c.Events)
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

    public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
        => _db.EventCategories.FindAsync([id], ct).AsTask();

    public Task<EventCategory?> GetCategoryWithEventsAsync(Guid id, CancellationToken ct = default)
        => _db.EventCategories.Include(c => c.Events).FirstOrDefaultAsync(c => c.Id == id, ct);

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

    public async Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
        => await _db.EventVenues
            .Where(v => v.IsActive)
            .OrderBy(v => v.DisplayOrder)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default)
        => await _db.EventVenues
            .Include(v => v.Events)
            .OrderBy(v => v.DisplayOrder)
            .ThenBy(v => v.Name)
            .ToListAsync(ct);

    public Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
        => _db.EventVenues.FindAsync([id], ct).AsTask();

    public Task<EventVenue?> GetVenueWithEventsAsync(Guid id, CancellationToken ct = default)
        => _db.EventVenues.Include(v => v.Events).FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default)
        => await _db.EventVenues.Select(v => (int?)v.DisplayOrder).MaxAsync(ct) ?? 0;

    public Task<List<EventVenue>> GetAllVenuesOrderedForSwapAsync(CancellationToken ct = default)
        => _db.EventVenues.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Name).ToListAsync(ct);

    public void Add(EventVenue venue) => _db.EventVenues.Add(venue);
    public void Remove(EventVenue venue) => _db.EventVenues.Remove(venue);

    // ── Events (submitter) ────────────────────────────────────────────────

    public async Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
        => await _db.Events
            .Include(e => e.Category)
            .Include(e => e.EventVenue)
            .Where(e => e.CampId == null && e.SubmitterUserId == userId)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync(ct);

    public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => _db.Events.FirstOrDefaultAsync(
            e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == userId, ct);

    public async Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
        => await _db.Events
            .Include(e => e.Category)
            .Where(e => e.CampId == campId)
            .OrderByDescending(e => e.SubmittedAt)
            .ToListAsync(ct);

    public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
        => _db.Events.FirstOrDefaultAsync(
            e => e.Id == eventId && e.CampId == campId, ct);

    public void Add(Event guideEvent) => _db.Events.Add(guideEvent);

    // ── Events (browse / export / API) ────────────────────────────────────

    public async Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
    {
        var query = _db.Events
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.EventVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)
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

    public Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Events
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.EventVenue)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)
            .FirstOrDefaultAsync(e => e.Id == id && e.Status == EventStatus.Approved, ct);

    public async Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
        => await _db.Events
            .Include(e => e.Category)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .ToListAsync(ct);

    // ── Events (moderation) ───────────────────────────────────────────────

    public async Task<Dictionary<EventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default)
    {
        var moderationStatuses = new[]
        {
            EventStatus.Pending,
            EventStatus.Approved,
            EventStatus.Rejected,
            EventStatus.ResubmitRequested,
        };

        var groups = await _db.Events
            .Where(e => moderationStatuses.Contains(e.Status))
            .GroupBy(e => e.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return groups.ToDictionary(g => g.Status, g => g.Count);
    }

    public async Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
    {
        var query = _db.Events
            .Include(e => e.Category)
            .Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)
            .Include(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(e => e.EventVenue)
            .Include(e => e.EventModerationActions)
            .Where(e => e.Status == status);

        query = status == EventStatus.Pending
            ? query.OrderBy(e => e.SubmittedAt)
            : query.OrderByDescending(e => e.SubmittedAt);

        return await query.ToListAsync(ct);
    }

    public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => _db.Events
            .Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)
            .Include(e => e.Camp)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    public async Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Events
            .Where(e => e.CampId != null &&
                        (e.Status == EventStatus.Pending || e.Status == EventStatus.Approved))
            .Select(e => new { e.Id, e.CampId, e.Title, e.StartAt, e.DurationMinutes, e.Status })
            .ToListAsync(ct);

        return rows.Select(r => new CampEventOverlap(r.Id, r.CampId, r.Title, r.StartAt, r.DurationMinutes, r.Status))
                   .ToList();
    }

    public void Add(EventModerationAction action) => _db.EventModerationActions.Add(action);

    // ── Favourites ────────────────────────────────────────────────────────

    public async Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var ids = await _db.EventFavourites
            .Where(f => f.UserId == userId)
            .Select(f => f.GuideEventId)
            .ToListAsync(ct);
        return [.. ids];
    }

    public async Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
        => await _db.EventFavourites
            .Include(f => f.Event).ThenInclude(e => e.Category)
            .Include(f => f.Event).ThenInclude(e => e.Camp!).ThenInclude(c => c.Seasons)
            .Include(f => f.Event).ThenInclude(e => e.EventVenue)
            .Where(f => f.UserId == userId && f.Event.Status == EventStatus.Approved)
            .OrderBy(f => f.Event.StartAt)
            .ToListAsync(ct);

    public Task<EventFavourite?> GetFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _db.EventFavourites.FirstOrDefaultAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);

    public Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _db.EventFavourites.AnyAsync(f => f.UserId == userId && f.GuideEventId == eventId, ct);

    public void Add(EventFavourite fav) => _db.EventFavourites.Add(fav);
    public void Remove(EventFavourite fav) => _db.EventFavourites.Remove(fav);

    // ── Preferences ───────────────────────────────────────────────────────

    public Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
        => _db.EventPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public void Add(EventPreference pref) => _db.EventPreferences.Add(pref);

    // ── Persistence ───────────────────────────────────────────────────────

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

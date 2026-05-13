using System.Text.Json;
using Humans.Application.DTOs.EventGuide;
using Humans.Application.Interfaces.EventGuide;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.EventGuide;

public sealed class EventGuideService : IEventGuideService
{
    private readonly IEventGuideRepository _repo;
    private readonly IClock _clock;

    public EventGuideService(IEventGuideRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    // ── Settings ─────────────────────────────────────────────────────────

    public Task<GuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
        => _repo.GetGuideSettingsAsync(ct);

    public async Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default)
    {
        var settings = await _repo.GetGuideSettingsAsync(ct);
        if (settings == null) return false;
        var now = _clock.GetCurrentInstant();
        return now >= settings.SubmissionOpenAt && now <= settings.SubmissionCloseAt;
    }

    public Task<IReadOnlyList<EventSettings>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
        => _repo.GetActiveEventSettingsAsync(ct);

    public Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetEventSettingsByIdAsync(id, ct);

    public async Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var eventSettings = await _repo.GetEventSettingsByIdAsync(eventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId);
        var now = _clock.GetCurrentInstant();

        var existing = await _repo.GetGuideSettingsAsync(ct);
        if (existing == null)
        {
            _repo.Add(new GuideSettings
            {
                Id = Guid.NewGuid(),
                EventSettingsId = eventSettingsId,
                SubmissionOpenAt = ToInstant(submissionOpenAt, tz),
                SubmissionCloseAt = ToInstant(submissionCloseAt, tz),
                GuidePublishAt = ToInstant(guidePublishAt, tz),
                MaxPrintSlots = maxPrintSlots,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.EventSettingsId = eventSettingsId;
            existing.SubmissionOpenAt = ToInstant(submissionOpenAt, tz);
            existing.SubmissionCloseAt = ToInstant(submissionCloseAt, tz);
            existing.GuidePublishAt = ToInstant(guidePublishAt, tz);
            existing.MaxPrintSlots = maxPrintSlots;
            existing.UpdatedAt = now;
        }

        await _repo.SaveChangesAsync(ct);
    }

    // ── Categories ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
        => _repo.GetActiveCategoriesAsync(ct);

    public Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
        => _repo.GetAllCategoriesAsync(ct);

    public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
        => _repo.GetCategoryAsync(id, ct);

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
        => _repo.CategorySlugExistsAsync(slug, excludeId, ct);

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default)
        => await _repo.GetMaxCategoryOrderAsync(ct) + 1;

    public async Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
    {
        _repo.Add(category);
        await _repo.SaveChangesAsync(ct);
    }

    public async Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => await _repo.SaveChangesAsync(ct);

    public async Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
    {
        var category = await _repo.GetCategoryWithEventsAsync(id, ct);
        if (category == null) return (false, -1);
        if (category.GuideEvents.Count > 0) return (false, category.GuideEvents.Count);

        _repo.Remove(category);
        await _repo.SaveChangesAsync(ct);
        return (true, 0);
    }

    public async Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var categories = await _repo.GetAllCategoriesOrderedForSwapAsync(ct);
        var index = categories.FindIndex(c => c.Id == id);
        if (index < 0) return;
        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= categories.Count) return;
        (categories[index].DisplayOrder, categories[targetIndex].DisplayOrder) =
            (categories[targetIndex].DisplayOrder, categories[index].DisplayOrder);
        await _repo.SaveChangesAsync(ct);
    }

    // ── Venues ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<GuideSharedVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
        => _repo.GetActiveVenuesAsync(ct);

    public Task<IReadOnlyList<GuideSharedVenue>> GetAllVenuesAsync(CancellationToken ct = default)
        => _repo.GetAllVenuesAsync(ct);

    public Task<GuideSharedVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
        => _repo.GetVenueAsync(id, ct);

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
        => await _repo.GetMaxVenueOrderAsync(ct) + 1;

    public async Task CreateVenueAsync(GuideSharedVenue venue, CancellationToken ct = default)
    {
        _repo.Add(venue);
        await _repo.SaveChangesAsync(ct);
    }

    public async Task UpdateVenueAsync(GuideSharedVenue venue, CancellationToken ct = default)
        => await _repo.SaveChangesAsync(ct);

    public async Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
    {
        var venue = await _repo.GetVenueWithEventsAsync(id, ct);
        if (venue == null) return (false, -1);
        if (venue.GuideEvents.Count > 0) return (false, venue.GuideEvents.Count);
        _repo.Remove(venue);
        await _repo.SaveChangesAsync(ct);
        return (true, 0);
    }

    public async Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
    {
        var venues = await _repo.GetAllVenuesOrderedForSwapAsync(ct);
        var index = venues.FindIndex(v => v.Id == id);
        if (index < 0) return;
        var targetIndex = index + direction;
        if (targetIndex < 0 || targetIndex >= venues.Count) return;
        (venues[index].DisplayOrder, venues[targetIndex].DisplayOrder) =
            (venues[targetIndex].DisplayOrder, venues[index].DisplayOrder);
        await _repo.SaveChangesAsync(ct);
    }

    // ── Submissions ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<GuideEvent>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetUserSubmissionsAsync(userId, ct);

    public Task<GuideEvent?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => _repo.GetUserEventAsync(eventId, userId, ct);

    public Task<IReadOnlyList<GuideEvent>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
        => _repo.GetCampSubmissionsAsync(campId, ct);

    public Task<GuideEvent?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
        => _repo.GetCampEventAsync(eventId, campId, ct);

    public async Task SubmitEventAsync(GuideEvent guideEvent, CancellationToken ct = default)
    {
        _repo.Add(guideEvent);
        await _repo.SaveChangesAsync(ct);
    }

    public async Task UpdateAndResubmitAsync(GuideEvent guideEvent, CancellationToken ct = default)
    {
        guideEvent.Submit(_clock);
        await _repo.SaveChangesAsync(ct);
    }

    public async Task WithdrawEventAsync(GuideEvent guideEvent, CancellationToken ct = default)
    {
        guideEvent.Withdraw(_clock);
        await _repo.SaveChangesAsync(ct);
    }

    // ── Browse / API ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<GuideEvent>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
        => _repo.GetApprovedEventsAsync(campId, venueId, categoryId, q, excludedSlugs, ct);

    public Task<GuideEvent?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetApprovedEventByIdAsync(id, ct);

    // ── Favourites ────────────────────────────────────────────────────────

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetFavouriteEventIdsAsync(userId, ct);

    public Task<IReadOnlyList<UserEventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetFavouritesWithEventsAsync(userId, ct);

    public async Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
    {
        var existing = await _repo.GetFavouriteAsync(userId, eventId, ct);
        if (existing != null)
        {
            _repo.Remove(existing);
        }
        else
        {
            _repo.Add(new UserEventFavourite
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                GuideEventId = eventId,
                CreatedAt = _clock.GetCurrentInstant()
            });
        }
        await _repo.SaveChangesAsync(ct);
    }

    public async Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
    {
        if (await _repo.FavouriteExistsAsync(userId, eventId, ct)) return false;
        _repo.Add(new UserEventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuideEventId = eventId,
            CreatedAt = _clock.GetCurrentInstant()
        });
        await _repo.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
    {
        var fav = await _repo.GetFavouriteAsync(userId, eventId, ct);
        if (fav == null) return false;
        _repo.Remove(fav);
        await _repo.SaveChangesAsync(ct);
        return true;
    }

    // ── Preferences ───────────────────────────────────────────────────────

    public async Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await _repo.GetPreferenceAsync(userId, ct);
        if (pref == null) return [];
        return JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [];
    }

    public Task<UserGuidePreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetPreferenceAsync(userId, ct);

    public async Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default)
    {
        var pref = await _repo.GetPreferenceAsync(userId, ct);
        var json = JsonSerializer.Serialize(slugs);
        if (pref == null)
        {
            _repo.Add(new UserGuidePreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ExcludedCategorySlugs = json,
                UpdatedAt = _clock.GetCurrentInstant()
            });
        }
        else
        {
            pref.ExcludedCategorySlugs = json;
            pref.UpdatedAt = _clock.GetCurrentInstant();
        }
        await _repo.SaveChangesAsync(ct);
    }

    // ── Moderation ────────────────────────────────────────────────────────

    public Task<Dictionary<GuideEventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default)
        => _repo.GetModerationStatusCountsAsync(ct);

    public Task<IReadOnlyList<GuideEvent>> GetEventsByStatusAsync(GuideEventStatus status, CancellationToken ct = default)
        => _repo.GetEventsByStatusAsync(status, ct);

    public Task<GuideEvent?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => _repo.GetEventForModerationAsync(eventId, ct);

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default)
        => _repo.GetActiveCampEventsAsync(ct);

    public async Task ApplyModerationAsync(
        Guid eventId, Guid actorUserId, ModerationActionType actionType, string? reason, CancellationToken ct = default)
    {
        var guideEvent = await _repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"GuideEvent {eventId} not found.");

        guideEvent.ApplyModerationAction(actionType, _clock);

        _repo.Add(new ModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = actorUserId,
            Action = actionType,
            Reason = reason,
            CreatedAt = _clock.GetCurrentInstant()
        });

        await _repo.SaveChangesAsync(ct);
    }

    // ── Dashboard / Export ────────────────────────────────────────────────

    public Task<IReadOnlyList<GuideEvent>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
        => _repo.GetAllEventsForDashboardAsync(ct);

    public async Task<(IReadOnlyList<GuideEvent> Events, GuideSettings? Settings)> GetApprovedEventsForExportAsync(CancellationToken ct = default)
    {
        var settings = await _repo.GetGuideSettingsAsync(ct);
        var events = await _repo.GetApprovedEventsAsync(null, null, null, null, [], ct);
        return (events, settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Instant ToInstant(LocalDateTime localDateTime, DateTimeZone? tz)
    {
        if (tz == null)
        {
            var utc = DateTime.SpecifyKind(localDateTime.ToDateTimeUnspecified(), DateTimeKind.Utc);
            return Instant.FromDateTimeUtc(utc);
        }
        return localDateTime.InZoneLeniently(tz).ToInstant();
    }
}

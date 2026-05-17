using System.Text.Json;
using Humans.Application.DTOs.Events;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Events;

public sealed class EventService : IEventService, IUserDataContributor
{
    private readonly IEventRepository _repo;
    // EventSettings is owned by the Shifts section (event_settings table).
    // Route reads through the read-only IBurnSettingsService supplier API,
    // which returns a BurnSettingsInfo DTO — the Events section never sees
    // the Shifts-internal entity (design-rules §2c,
    // memory/architecture/no-cross-section-ef-joins.md, issue nobodies-collective/Humans#719).
    private readonly IBurnSettingsService _burnSettings;
    private readonly IClock _clock;

    public EventService(IEventRepository repo, IBurnSettingsService burnSettings, IClock clock)
    {
        _repo = repo;
        _burnSettings = burnSettings;
        _clock = clock;
    }

    // ── Settings ─────────────────────────────────────────────────────────

    public Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
        => _repo.GetGuideSettingsAsync(ct);

    public async Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default)
    {
        var settings = await _repo.GetGuideSettingsAsync(ct);
        return settings?.IsSubmissionOpenAt(_clock.GetCurrentInstant()) ?? false;
    }

    public async Task<IReadOnlyList<BurnSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
    {
        // Invariant: at most one active burn. Surface it as a singleton list
        // so the admin picker (which calls this) stays shape-compatible if
        // multi-burn support arrives later.
        var active = await _burnSettings.GetActiveAsync(ct);
        return active is null ? [] : [active];
    }

    public Task<BurnSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => _burnSettings.GetByIdAsync(id, ct);

    public async Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var burn = await _burnSettings.GetByIdAsync(eventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        var now = _clock.GetCurrentInstant();

        var settings = new EventGuideSettings
        {
            Id = existingId ?? Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            SubmissionOpenAt = ToInstant(submissionOpenAt, tz),
            SubmissionCloseAt = ToInstant(submissionCloseAt, tz),
            GuidePublishAt = ToInstant(guidePublishAt, tz),
            MaxPrintSlots = maxPrintSlots,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repo.UpsertGuideSettingsAsync(settings, ct);
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

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => _repo.AddCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => _repo.SaveCategoryAsync(category, ct);

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteCategoryAsync(id, ct);

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
        => _repo.SwapCategoryOrderAsync(id, direction, ct);

    // ── Venues ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
        => _repo.GetActiveVenuesAsync(ct);

    public Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default)
        => _repo.GetAllVenuesAsync(ct);

    public Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
        => _repo.GetVenueAsync(id, ct);

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
        => await _repo.GetMaxVenueOrderAsync(ct) + 1;

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => _repo.AddVenueAsync(venue, ct);

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => _repo.SaveVenueAsync(venue, ct);

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteVenueAsync(id, ct);

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
        => _repo.SwapVenueOrderAsync(id, direction, ct);

    // ── Submissions ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetUserSubmissionsAsync(userId, ct);

    public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => _repo.GetUserEventAsync(eventId, userId, ct);

    public Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
        => _repo.GetCampSubmissionsAsync(campId, ct);

    public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
        => _repo.GetCampEventAsync(eventId, campId, ct);

    public Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default)
        => _repo.AddEventAsync(guideEvent, ct);

    public Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Submit(_clock);
        return _repo.SaveEventAsync(guideEvent, ct);
    }

    public Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Withdraw(_clock);
        return _repo.SaveEventAsync(guideEvent, ct);
    }

    // ── Browse / API ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
        => _repo.GetApprovedEventsAsync(campId, venueId, categoryId, q, excludedSlugs, ct);

    public Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetApprovedEventByIdAsync(id, ct);

    // ── Favourites ────────────────────────────────────────────────────────

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetFavouriteEventIdsAsync(userId, ct);

    public Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetFavouritesWithEventsAsync(userId, ct);

    public Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _repo.ToggleFavouriteAsync(userId, eventId, BuildFavourite(userId, eventId), ct);

    public Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _repo.AddFavouriteIfAbsentAsync(BuildFavourite(userId, eventId), ct);

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => _repo.RemoveFavouriteAsync(userId, eventId, ct);

    // ── Preferences ───────────────────────────────────────────────────────

    public async Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await _repo.GetPreferenceAsync(userId, ct);
        if (pref == null) return [];
        return JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [];
    }

    public Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
        => _repo.GetPreferenceAsync(userId, ct);

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default)
        => _repo.UpsertPreferenceAsync(userId, JsonSerializer.Serialize(slugs), _clock.GetCurrentInstant(), ct);

    // ── Moderation ────────────────────────────────────────────────────────

    public Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default)
        => _repo.GetModerationStatusCountsAsync(ct);

    public Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
        => _repo.GetEventsByStatusAsync(status, ct);

    public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => _repo.GetEventForModerationAsync(eventId, ct);

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default)
        => _repo.GetActiveCampEventsAsync(ct);

    public async Task ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, CancellationToken ct = default)
    {
        var guideEvent = await _repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

        guideEvent.ApplyModerationAction(actionType, _clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = actorUserId,
            Action = actionType,
            Reason = reason,
            CreatedAt = _clock.GetCurrentInstant()
        };

        await _repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
    }

    // ── Dashboard / Export ────────────────────────────────────────────────

    public Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
        => _repo.GetAllEventsForDashboardAsync(ct);

    public async Task<(IReadOnlyList<Event> Events, EventGuideSettings? Settings)> GetApprovedEventsForExportAsync(CancellationToken ct = default)
    {
        var settings = await _repo.GetGuideSettingsAsync(ct);
        var events = await _repo.GetApprovedEventsAsync(null, null, null, null, [], ct);
        return (events, settings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private EventFavourite BuildFavourite(Guid userId, Guid eventId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GuideEventId = eventId,
        CreatedAt = _clock.GetCurrentInstant()
    };

    private static Instant ToInstant(LocalDateTime localDateTime, DateTimeZone? tz)
    {
        if (tz == null)
        {
            var utc = DateTime.SpecifyKind(localDateTime.ToDateTimeUnspecified(), DateTimeKind.Utc);
            return Instant.FromDateTimeUtc(utc);
        }
        return localDateTime.InZoneLeniently(tz).ToInstant();
    }

    // ── GDPR Article 15 contributor ───────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var favourites = await _repo.GetFavouritesForContributorAsync(userId, ct);
        var preference = await _repo.GetPreferenceAsync(userId, ct);

        var shaped = new
        {
            Favourites = favourites
                .OrderBy(f => f.CreatedAt)
                .Select(f => new
                {
                    f.GuideEventId,
                    CreatedAt = f.CreatedAt.ToInvariantInstantString()
                }).ToList(),
            Preference = preference == null ? null : new
            {
                preference.ExcludedCategorySlugs,
                UpdatedAt = preference.UpdatedAt.ToInvariantInstantString()
            }
        };

        return [new UserDataSlice(GdprExportSections.Events, shaped)];
    }
}

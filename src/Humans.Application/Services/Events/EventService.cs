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

public sealed class EventService(IEventRepository repo, IBurnSettingsService burnSettings, IClock clock)
    : IEventService, IUserDataContributor
{
    // EventSettings is owned by Shifts; cross via IBurnSettingsService supplier API (§2c, #719).

    public Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
        => repo.GetGuideSettingsAsync(ct);

    public async Task<bool> IsSubmissionOpenAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        return settings?.IsSubmissionOpenAt(clock.GetCurrentInstant()) ?? false;
    }

    public async Task<IReadOnlyList<BurnSettingsInfo>> GetEventSettingsOptionsAsync(CancellationToken ct = default)
    {
        // Invariant: at most one active burn; singleton list keeps admin picker forward-compatible.
        var active = await burnSettings.GetActiveAsync(ct);
        return active is null ? [] : [active];
    }

    public Task<BurnSettingsInfo?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
        => burnSettings.GetByIdAsync(id, ct);

    public async Task SaveGuideSettingsAsync(
        Guid? existingId, Guid eventSettingsId,
        LocalDateTime submissionOpenAt, LocalDateTime submissionCloseAt, LocalDateTime guidePublishAt,
        int maxPrintSlots, CancellationToken ct = default)
    {
        var burn = await burnSettings.GetByIdAsync(eventSettingsId, ct)
            ?? throw new InvalidOperationException($"EventSettings {eventSettingsId} not found.");

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId);
        var now = clock.GetCurrentInstant();

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

        await repo.UpsertGuideSettingsAsync(settings, ct);
    }

    public Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
        => repo.GetActiveCategoriesAsync(ct);

    public Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
        => repo.GetAllCategoriesAsync(ct);

    public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
        => repo.GetCategoryAsync(id, ct);

    public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId = null, CancellationToken ct = default)
        => repo.CategorySlugExistsAsync(slug, excludeId, ct);

    public async Task<int> GetNextCategoryOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxCategoryOrderAsync(ct) + 1;

    public Task CreateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.AddCategoryAsync(category, ct);

    public Task UpdateCategoryAsync(EventCategory category, CancellationToken ct = default)
        => repo.SaveCategoryAsync(category, ct);

    public Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteCategoryAsync(id, ct);

    public Task MoveCategoryAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapCategoryOrderAsync(id, direction, ct);

    public Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
        => repo.GetActiveVenuesAsync(ct);

    public Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default)
        => repo.GetAllVenuesAsync(ct);

    public Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
        => repo.GetVenueAsync(id, ct);

    public async Task<int> GetNextVenueOrderAsync(CancellationToken ct = default)
        => await repo.GetMaxVenueOrderAsync(ct) + 1;

    public Task CreateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.AddVenueAsync(venue, ct);

    public Task UpdateVenueAsync(EventVenue venue, CancellationToken ct = default)
        => repo.SaveVenueAsync(venue, ct);

    public Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default)
        => repo.DeleteVenueAsync(id, ct);

    public Task MoveVenueAsync(Guid id, int direction, CancellationToken ct = default)
        => repo.SwapVenueOrderAsync(id, direction, ct);

    public Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetUserSubmissionsAsync(userId, ct);

    public Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
        => repo.GetUserEventAsync(eventId, userId, ct);

    public Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
        => repo.GetCampSubmissionsAsync(campId, ct);

    public Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
        => repo.GetCampEventAsync(eventId, campId, ct);

    public Task SubmitEventAsync(Event guideEvent, CancellationToken ct = default)
        => repo.AddEventAsync(guideEvent, ct);

    public Task UpdateAndResubmitAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Submit(clock);
        return repo.SaveEventAsync(guideEvent, ct);
    }

    public Task WithdrawEventAsync(Event guideEvent, CancellationToken ct = default)
    {
        guideEvent.Withdraw(clock);
        return repo.SaveEventAsync(guideEvent, ct);
    }

    public Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
        => repo.GetApprovedEventsAsync(campId, venueId, categoryId, q, excludedSlugs, ct);

    public Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
        => repo.GetApprovedEventByIdAsync(id, ct);

    public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetFavouriteEventIdsAsync(userId, ct);

    public Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
        => repo.GetFavouritesWithEventsAsync(userId, ct);

    public Task ToggleFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.ToggleFavouriteAsync(userId, eventId, BuildFavourite(userId, eventId), ct);

    public Task<bool> AddFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.AddFavouriteIfAbsentAsync(BuildFavourite(userId, eventId), ct);

    public Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
        => repo.RemoveFavouriteAsync(userId, eventId, ct);

    public async Task<List<string>> GetExcludedCategorySlugsAsync(Guid userId, CancellationToken ct = default)
    {
        var pref = await repo.GetPreferenceAsync(userId, ct);
        if (pref == null) return [];
        return JsonSerializer.Deserialize<List<string>>(pref.ExcludedCategorySlugs) ?? [];
    }

    public Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
        => repo.GetPreferenceAsync(userId, ct);

    public Task SavePreferenceAsync(Guid userId, List<string> slugs, CancellationToken ct = default)
        => repo.UpsertPreferenceAsync(userId, JsonSerializer.Serialize(slugs), clock.GetCurrentInstant(), ct);

    public Task<Dictionary<EventStatus, int>> GetEventStatusCountsAsync(CancellationToken ct = default)
        => repo.GetModerationStatusCountsAsync(ct);

    public Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default)
        => repo.GetEventsByStatusAsync(status, ct);

    public Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
        => repo.GetEventForModerationAsync(eventId, ct);

    public Task<IReadOnlyList<CampEventOverlap>> GetCampEventsForOverlapAsync(CancellationToken ct = default)
        => repo.GetActiveCampEventsAsync(ct);

    public async Task ApplyModerationAsync(
        Guid eventId, Guid actorUserId, EventModerationActionType actionType, string? reason, CancellationToken ct = default)
    {
        var guideEvent = await repo.GetEventForModerationAsync(eventId, ct)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");

        guideEvent.ApplyModerationAction(actionType, clock);

        var action = new EventModerationAction
        {
            Id = Guid.NewGuid(),
            GuideEventId = eventId,
            ActorUserId = actorUserId,
            Action = actionType,
            Reason = reason,
            CreatedAt = clock.GetCurrentInstant()
        };

        await repo.SaveEventAndModerationActionAsync(guideEvent, action, ct);
    }

    public Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
        => repo.GetAllEventsForDashboardAsync(ct);

    public async Task<(IReadOnlyList<Event> Events, EventGuideSettings? Settings)> GetApprovedEventsForExportAsync(CancellationToken ct = default)
    {
        var settings = await repo.GetGuideSettingsAsync(ct);
        var events = await repo.GetApprovedEventsAsync(null, null, null, null, [], ct);
        return (events, settings);
    }

    private EventFavourite BuildFavourite(Guid userId, Guid eventId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        GuideEventId = eventId,
        CreatedAt = clock.GetCurrentInstant()
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

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var favourites = await repo.GetFavouritesForContributorAsync(userId, ct);
        var preference = await repo.GetPreferenceAsync(userId, ct);

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

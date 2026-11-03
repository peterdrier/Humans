using Humans.Events.Services.Dtos;
using Humans.Events.Domain;
using Humans.Base.Interfaces.Repositories;
using Humans.Events.Contracts;

namespace Humans.Events.Data;

internal interface IEventRepository : IRepository
{
    Task<EventGuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default);

    Task UpsertGuideSettingsAsync(EventGuideSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default);
    /// <summary>Active and inactive, with <c>.Events</c> included.</summary>
    Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default);
    Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default);
    Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default);
    Task AddCategoryAsync(EventCategory category, CancellationToken ct = default);
    Task SaveCategoryAsync(EventCategory category, CancellationToken ct = default);
    /// <summary>Deletes when no events reference the category. Returns (deleted, linkedCount); linkedCount=-1 means not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task SwapCategoryOrderAsync(Guid id, int direction, CancellationToken ct = default);

    Task<IReadOnlyList<EventVenue>> GetActiveVenuesAsync(CancellationToken ct = default);
    /// <summary>Active and inactive, with <c>.Events</c> included.</summary>
    Task<IReadOnlyList<EventVenue>> GetAllVenuesAsync(CancellationToken ct = default);
    Task<EventVenue?> GetVenueAsync(Guid id, CancellationToken ct = default);
    Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default);
    Task AddVenueAsync(EventVenue venue, CancellationToken ct = default);
    Task SaveVenueAsync(EventVenue venue, CancellationToken ct = default);
    /// <summary>Deletes when no events reference the venue. Returns (deleted, linkedCount); linkedCount=-1 means not found.</summary>
    Task<(bool deleted, int linkedCount)> DeleteVenueAsync(Guid id, CancellationToken ct = default);
    Task SwapVenueOrderAsync(Guid id, int direction, CancellationToken ct = default);

    Task<IReadOnlyList<Event>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default);
    Task<Event?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default);
    Task<Event?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default);
    Task AddEventAsync(Event guideEvent, CancellationToken ct = default);
    Task SaveEventAsync(Event guideEvent, CancellationToken ct = default);
    /// <summary>Commits all validated import additions and edits together; no-op when both lists are empty.</summary>
    Task SaveImportedEventsAsync(IReadOnlyList<Event> created, IReadOnlyList<Event> updated, CancellationToken ct = default);
    /// <summary>Only rows still without an event id are written. Returns the rows written.</summary>
    Task<int> AssignEventSettingsIdsAsync(IReadOnlyDictionary<Guid, Guid> eventSettingsIdByEventId, CancellationToken ct = default);

    Task<IReadOnlyList<Event>> GetApprovedEventsAsync(
        Guid? campId, Guid? venueId, Guid? categoryId, string? q,
        IReadOnlyList<string> excludedSlugs, CancellationToken ct = default);
    Task<Event?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetAllEventsForDashboardAsync(CancellationToken ct = default);

    /// <summary>Scoped like <see cref="Event.IsInEvent"/>.</summary>
    Task<Dictionary<EventStatus, int>> GetModerationStatusCountsAsync(Guid? eventSettingsId, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> GetEventsByStatusAsync(EventStatus status, CancellationToken ct = default);
    Task<Event?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default);
    /// <summary>Read-only load with Category, EventVenue and moderation history; never pass the result to a save.</summary>
    Task<Event?> GetEventWithDetailsAsync(Guid eventId, CancellationToken ct = default);
    /// <summary>Scoped like <see cref="Event.IsInEvent"/>.</summary>
    Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(Guid? eventSettingsId, CancellationToken ct = default);
    /// <summary>Persists the already-transitioned event and appends the action in one transaction.</summary>
    Task SaveEventAndModerationActionAsync(Event guideEvent, EventModerationAction action, CancellationToken ct = default);

    Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<EventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default);
    /// <summary>
    /// Adds only when absent. A favourite matches on the same day offset or a whole-event (null) row; a whole-event
    /// reference matches every row for the event. False when a match already existed.
    /// </summary>
    Task<bool> AddFavouriteIfAbsentAsync(EventFavourite favourite, CancellationToken ct = default);
    /// <summary>Same match rule as add. False when none existed.</summary>
    Task<bool> RemoveFavouriteAsync(Guid userId, Guid eventId, int? dayOffset, CancellationToken ct = default);

    Task<EventPreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default);
    Task UpsertPreferenceAsync(Guid userId, string excludedCategorySlugsJson, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>Regardless of the event's status.</summary>
    Task<IReadOnlyList<EventFavourite>> GetFavouritesForContributorAsync(Guid userId, CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes the user's favourites and their category-exclusion preference.</summary>
    Task<int> DeleteFavouritesAndPreferenceForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: clears <c>Host</c> on every event the user submitted. The submission itself is retained (see
    /// the section's erasure declaration).
    /// </summary>
    Task<int> ClearSubmitterHostForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// A source favourite the target already covers is deleted. When both have a preference the target's wins and the
    /// source row stays as archived history. Idempotent.
    /// </summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, NodaTime.Instant updatedAt, CancellationToken ct = default);
}

using AwesomeAssertions;
using Humans.Application.DTOs.EventGuide;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.EventGuide;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public sealed class EventGuideServiceTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 5, 12, 0));
    private readonly FakeEventGuideRepository _repo = new();
    private readonly EventGuideService _service;

    public EventGuideServiceTests()
    {
        _service = new EventGuideService(_repo, _clock);
    }

    [HumansFact]
    public async Task IsSubmissionOpenAsync_ReturnsFalse_WhenSettingsMissing()
    {
        var result = await _service.IsSubmissionOpenAsync();

        result.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    public async Task IsSubmissionOpenAsync_UsesInclusiveOpenAndCloseWindow(int hour, bool expected)
    {
        _clock.Reset(Instant.FromUtc(2026, 5, 5, hour, 0));
        _repo.Settings = new GuideSettings
        {
            Id = Guid.NewGuid(),
            EventSettingsId = Guid.NewGuid(),
            SubmissionOpenAt = Instant.FromUtc(2026, 5, 5, 12, 0),
            SubmissionCloseAt = Instant.FromUtc(2026, 5, 5, 13, 0),
            GuidePublishAt = Instant.FromUtc(2026, 5, 6, 12, 0),
            MaxPrintSlots = 10,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        var result = await _service.IsSubmissionOpenAsync();

        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task SaveGuideSettingsAsync_CreatesSettingsUsingEventTimezone()
    {
        var eventSettingsId = Guid.NewGuid();
        _repo.EventSettings[eventSettingsId] = new EventSettings
        {
            Id = eventSettingsId,
            EventName = "Nowhere 2026",
            Year = 2026,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        await _service.SaveGuideSettingsAsync(
            existingId: null,
            eventSettingsId,
            submissionOpenAt: new LocalDateTime(2026, 5, 5, 12, 0),
            submissionCloseAt: new LocalDateTime(2026, 5, 6, 12, 0),
            guidePublishAt: new LocalDateTime(2026, 5, 7, 12, 0),
            maxPrintSlots: 42);

        _repo.Settings.Should().NotBeNull();
        _repo.Settings!.SubmissionOpenAt.Should().Be(Instant.FromUtc(2026, 5, 5, 10, 0));
        _repo.Settings.SubmissionCloseAt.Should().Be(Instant.FromUtc(2026, 5, 6, 10, 0));
        _repo.Settings.GuidePublishAt.Should().Be(Instant.FromUtc(2026, 5, 7, 10, 0));
        _repo.Settings.MaxPrintSlots.Should().Be(42);
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SaveGuideSettingsAsync_Throws_WhenEventSettingsMissing()
    {
        var eventSettingsId = Guid.NewGuid();

        var act = () => _service.SaveGuideSettingsAsync(
            null,
            eventSettingsId,
            new LocalDateTime(2026, 5, 5, 12, 0),
            new LocalDateTime(2026, 5, 6, 12, 0),
            new LocalDateTime(2026, 5, 7, 12, 0),
            10);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"EventSettings {eventSettingsId} not found.");
    }

    [HumansFact]
    public async Task MoveCategoryAsync_SwapsDisplayOrderWithNeighbour()
    {
        var first = new EventCategory { Id = Guid.NewGuid(), Name = "A", Slug = "a", DisplayOrder = 1 };
        var second = new EventCategory { Id = Guid.NewGuid(), Name = "B", Slug = "b", DisplayOrder = 2 };
        _repo.Categories.AddRange([first, second]);

        await _service.MoveCategoryAsync(second.Id, direction: -1);

        second.DisplayOrder.Should().Be(1);
        first.DisplayOrder.Should().Be(2);
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task DeleteVenueAsync_ReturnsLinkedCountAndDoesNotRemove_WhenEventsReferenceVenue()
    {
        var venue = new GuideSharedVenue { Id = Guid.NewGuid(), Name = "Main Stage" };
        venue.GuideEvents.Add(new GuideEvent { Id = Guid.NewGuid(), Title = "Talk" });
        _repo.Venues.Add(venue);

        var result = await _service.DeleteVenueAsync(venue.Id);

        result.Should().Be((false, 1));
        _repo.RemovedVenues.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(0);
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_AddsFavourite_WhenMissing()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _service.ToggleFavouriteAsync(userId, eventId);

        _repo.Favourites.Should().ContainSingle(f =>
            f.UserId == userId
            && f.GuideEventId == eventId
            && f.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ToggleFavouriteAsync_RemovesFavourite_WhenExisting()
    {
        var fav = new UserEventFavourite
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            GuideEventId = Guid.NewGuid(),
            CreatedAt = _clock.GetCurrentInstant()
        };
        _repo.Favourites.Add(fav);

        await _service.ToggleFavouriteAsync(fav.UserId, fav.GuideEventId);

        _repo.Favourites.Should().BeEmpty();
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task SavePreferenceAsync_UpdatesExistingPreferenceJsonAndTimestamp()
    {
        var userId = Guid.NewGuid();
        _repo.Preference = new UserGuidePreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ExcludedCategorySlugs = "[]",
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 12, 0)
        };

        await _service.SavePreferenceAsync(userId, ["adult", "spiritual"]);

        _repo.Preference.ExcludedCategorySlugs.Should().Be("[\"adult\",\"spiritual\"]");
        _repo.Preference.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ApplyModerationAsync_TransitionsEventAndAppendsModerationAction()
    {
        var guideEvent = new GuideEvent
        {
            Id = Guid.NewGuid(),
            Status = GuideEventStatus.Pending,
            LastUpdatedAt = Instant.FromUtc(2026, 5, 1, 12, 0)
        };
        _repo.Events.Add(guideEvent);
        var actorUserId = Guid.NewGuid();

        await _service.ApplyModerationAsync(
            guideEvent.Id,
            actorUserId,
            ModerationActionType.ResubmitRequested,
            "Add location");

        guideEvent.Status.Should().Be(GuideEventStatus.ResubmitRequested);
        guideEvent.LastUpdatedAt.Should().Be(_clock.GetCurrentInstant());
        _repo.ModerationActions.Should().ContainSingle(action =>
            action.GuideEventId == guideEvent.Id
            && action.ActorUserId == actorUserId
            && action.Action == ModerationActionType.ResubmitRequested
            && action.Reason == "Add location"
            && action.CreatedAt == _clock.GetCurrentInstant());
        _repo.SaveChangesCount.Should().Be(1);
    }

    private sealed class FakeEventGuideRepository : IEventGuideRepository
    {
        public GuideSettings? Settings { get; set; }
        public Dictionary<Guid, EventSettings> EventSettings { get; } = [];
        public List<EventCategory> Categories { get; } = [];
        public List<GuideSharedVenue> Venues { get; } = [];
        public List<GuideSharedVenue> RemovedVenues { get; } = [];
        public List<GuideEvent> Events { get; } = [];
        public List<UserEventFavourite> Favourites { get; } = [];
        public List<ModerationAction> ModerationActions { get; } = [];
        public UserGuidePreference? Preference { get; set; }
        public int SaveChangesCount { get; private set; }

        public Task<GuideSettings?> GetGuideSettingsAsync(CancellationToken ct = default)
            => Task.FromResult(Settings);

        public Task<IReadOnlyList<EventSettings>> GetActiveEventSettingsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventSettings>>(EventSettings.Values.Where(e => e.IsActive).ToList());

        public Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(EventSettings.GetValueOrDefault(id));

        public void Add(GuideSettings settings) => Settings = settings;

        public Task<IReadOnlyList<EventCategory>> GetActiveCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventCategory>>(Categories.Where(c => c.IsActive).ToList());

        public Task<IReadOnlyList<EventCategory>> GetAllCategoriesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventCategory>>(Categories);

        public Task<EventCategory?> GetCategoryAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Categories.FirstOrDefault(c => c.Id == id));

        public Task<EventCategory?> GetCategoryWithEventsAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Categories.FirstOrDefault(c => c.Id == id));

        public Task<bool> CategorySlugExistsAsync(string slug, Guid? excludeId, CancellationToken ct = default)
            => Task.FromResult(Categories.Any(c => string.Equals(c.Slug, slug, StringComparison.Ordinal) && c.Id != excludeId));

        public Task<int> GetMaxCategoryOrderAsync(CancellationToken ct = default)
            => Task.FromResult(Categories.Count == 0 ? 0 : Categories.Max(c => c.DisplayOrder));

        public Task<List<EventCategory>> GetAllCategoriesOrderedForSwapAsync(CancellationToken ct = default)
            => Task.FromResult(Categories.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name, StringComparer.Ordinal).ToList());

        public void Add(EventCategory category) => Categories.Add(category);
        public void Remove(EventCategory category) => Categories.Remove(category);

        public Task<IReadOnlyList<GuideSharedVenue>> GetActiveVenuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideSharedVenue>>(Venues.Where(v => v.IsActive).ToList());

        public Task<IReadOnlyList<GuideSharedVenue>> GetAllVenuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideSharedVenue>>(Venues);

        public Task<GuideSharedVenue?> GetVenueAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Venues.FirstOrDefault(v => v.Id == id));

        public Task<GuideSharedVenue?> GetVenueWithEventsAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Venues.FirstOrDefault(v => v.Id == id));

        public Task<int> GetMaxVenueOrderAsync(CancellationToken ct = default)
            => Task.FromResult(Venues.Count == 0 ? 0 : Venues.Max(v => v.DisplayOrder));

        public Task<List<GuideSharedVenue>> GetAllVenuesOrderedForSwapAsync(CancellationToken ct = default)
            => Task.FromResult(Venues.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Name, StringComparer.Ordinal).ToList());

        public void Add(GuideSharedVenue venue) => Venues.Add(venue);

        public void Remove(GuideSharedVenue venue)
        {
            RemovedVenues.Add(venue);
            Venues.Remove(venue);
        }

        public Task<IReadOnlyList<GuideEvent>> GetUserSubmissionsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideEvent>>(Events.Where(e => e.CampId == null && e.SubmitterUserId == userId).ToList());

        public Task<GuideEvent?> GetUserEventAsync(Guid eventId, Guid userId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId && e.CampId == null && e.SubmitterUserId == userId));

        public Task<IReadOnlyList<GuideEvent>> GetCampSubmissionsAsync(Guid campId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideEvent>>(Events.Where(e => e.CampId == campId).ToList());

        public Task<GuideEvent?> GetCampEventAsync(Guid eventId, Guid campId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId && e.CampId == campId));

        public void Add(GuideEvent guideEvent) => Events.Add(guideEvent);

        public Task<IReadOnlyList<GuideEvent>> GetApprovedEventsAsync(Guid? campId, Guid? venueId, Guid? categoryId, string? q, IReadOnlyList<string> excludedSlugs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideEvent>>(Events.Where(e => e.Status == GuideEventStatus.Approved).ToList());

        public Task<GuideEvent?> GetApprovedEventByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == id && e.Status == GuideEventStatus.Approved));

        public Task<IReadOnlyList<GuideEvent>> GetAllEventsForDashboardAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideEvent>>(Events);

        public Task<Dictionary<GuideEventStatus, int>> GetModerationStatusCountsAsync(CancellationToken ct = default)
            => Task.FromResult(Events.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()));

        public Task<IReadOnlyList<GuideEvent>> GetEventsByStatusAsync(GuideEventStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GuideEvent>>(Events.Where(e => e.Status == status).ToList());

        public Task<GuideEvent?> GetEventForModerationAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Events.FirstOrDefault(e => e.Id == eventId));

        public Task<IReadOnlyList<CampEventOverlap>> GetActiveCampEventsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CampEventOverlap>>([]);

        public void Add(ModerationAction action) => ModerationActions.Add(action);

        public Task<HashSet<Guid>> GetFavouriteEventIdsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Favourites.Where(f => f.UserId == userId).Select(f => f.GuideEventId).ToHashSet());

        public Task<IReadOnlyList<UserEventFavourite>> GetFavouritesWithEventsAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserEventFavourite>>(Favourites.Where(f => f.UserId == userId).ToList());

        public Task<UserEventFavourite?> GetFavouriteAsync(Guid userId, Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Favourites.FirstOrDefault(f => f.UserId == userId && f.GuideEventId == eventId));

        public Task<bool> FavouriteExistsAsync(Guid userId, Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Favourites.Any(f => f.UserId == userId && f.GuideEventId == eventId));

        public void Add(UserEventFavourite fav) => Favourites.Add(fav);
        public void Remove(UserEventFavourite fav) => Favourites.Remove(fav);

        public Task<UserGuidePreference?> GetPreferenceAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Preference?.UserId == userId ? Preference : null);

        public void Add(UserGuidePreference pref) => Preference = pref;

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveChangesCount++;
            return Task.CompletedTask;
        }
    }
}

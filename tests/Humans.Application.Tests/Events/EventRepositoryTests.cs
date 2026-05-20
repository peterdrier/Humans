using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Events;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

public sealed class EventRepositoryTests : IDisposable
{
    private readonly HumansDbContext _db;
    private readonly EventRepository _repo;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 5, 12, 0));

    public EventRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new HumansDbContext(options);
        _repo = new EventRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [HumansFact]
    public async Task GetActiveCategoriesAsync_ReturnsOnlyActiveInDisplayOrder()
    {
        var inactive = SeedCategory("Inactive", "inactive", 0, isActive: false);
        var second = SeedCategory("Second", "second", 2);
        var first = SeedCategory("First", "first", 1);
        await _db.SaveChangesAsync();

        var result = await _repo.GetActiveCategoriesAsync();

        result.Select(c => c.Id).Should().Equal(first.Id, second.Id);
        result.Should().NotContain(c => c.Id == inactive.Id);
    }

    [HumansFact]
    public async Task CategorySlugExistsAsync_HonoursExcludeId()
    {
        var category = SeedCategory("Music", "music", 1);
        await _db.SaveChangesAsync();

        (await _repo.CategorySlugExistsAsync("music", excludeId: null)).Should().BeTrue();
        (await _repo.CategorySlugExistsAsync("music", excludeId: category.Id)).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetUserSubmissionsAsync_ReturnsOnlyIndividualEventsForSubmitterNewestFirst()
    {
        var category = SeedCategory("Workshop", "workshop", 1);
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var older = SeedEvent(category.Id, userId, EventStatus.Pending, submittedAt: Instant.FromUtc(2026, 5, 1, 12, 0));
        var newer = SeedEvent(category.Id, userId, EventStatus.Approved, submittedAt: Instant.FromUtc(2026, 5, 2, 12, 0));
        SeedEvent(category.Id, otherUserId, EventStatus.Pending, submittedAt: Instant.FromUtc(2026, 5, 3, 12, 0));
        SeedEvent(category.Id, userId, EventStatus.Pending, submittedAt: Instant.FromUtc(2026, 5, 4, 12, 0), campId: Guid.NewGuid());
        await _db.SaveChangesAsync();

        var result = await _repo.GetUserSubmissionsAsync(userId);

        result.Select(e => e.Id).Should().Equal(newer.Id, older.Id);
    }

    [HumansFact]
    public async Task GetApprovedEventsAsync_FiltersStatusCategoryVenueCampAndExcludedSlugs()
    {
        var includedCategory = SeedCategory("Music", "music", 1);
        var excludedCategory = SeedCategory("Adult", "adult", 2);
        var venue = SeedVenue("Main Stage", 1);
        var campId = Guid.NewGuid();
        var matching = SeedEvent(
            includedCategory.Id,
            Guid.NewGuid(),
            EventStatus.Approved,
            submittedAt: Instant.FromUtc(2026, 5, 1, 12, 0),
            startAt: Instant.FromUtc(2026, 7, 1, 10, 0),
            campId: campId,
            venueId: venue.Id);
        SeedEvent(includedCategory.Id, Guid.NewGuid(), EventStatus.Pending, submittedAt: _clock.GetCurrentInstant(), campId: campId, venueId: venue.Id);
        SeedEvent(excludedCategory.Id, Guid.NewGuid(), EventStatus.Approved, submittedAt: _clock.GetCurrentInstant(), campId: campId, venueId: venue.Id);
        SeedEvent(includedCategory.Id, Guid.NewGuid(), EventStatus.Approved, submittedAt: _clock.GetCurrentInstant(), campId: Guid.NewGuid(), venueId: venue.Id);
        await _db.SaveChangesAsync();

        var result = await _repo.GetApprovedEventsAsync(
            campId,
            venue.Id,
            includedCategory.Id,
            q: null,
            excludedSlugs: ["adult"]);

        result.Should().ContainSingle();
        result[0].Id.Should().Be(matching.Id);
    }

    [HumansFact]
    public async Task GetModerationStatusCountsAsync_CountsOnlyModerationStatuses()
    {
        var category = SeedCategory("Workshop", "workshop", 1);
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, _clock.GetCurrentInstant());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, _clock.GetCurrentInstant());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Approved, _clock.GetCurrentInstant());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Draft, _clock.GetCurrentInstant());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Withdrawn, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync();

        var result = await _repo.GetModerationStatusCountsAsync();

        result.Should().ContainKey(EventStatus.Pending).WhoseValue.Should().Be(2);
        result.Should().ContainKey(EventStatus.Approved).WhoseValue.Should().Be(1);
        result.Should().ContainKey(EventStatus.Withdrawn).WhoseValue.Should().Be(1);
        result.Should().NotContainKey(EventStatus.Draft);
    }

    [HumansFact]
    public async Task GetEventsByStatusAsync_OrdersPendingOldestFirst()
    {
        var category = SeedCategory("Workshop", "workshop", 1);
        var older = SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, Instant.FromUtc(2026, 5, 1, 12, 0));
        var newer = SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, Instant.FromUtc(2026, 5, 2, 12, 0));
        await _db.SaveChangesAsync();

        var result = await _repo.GetEventsByStatusAsync(EventStatus.Pending);

        result.Select(e => e.Id).Should().Equal(older.Id, newer.Id);
    }

    [HumansFact]
    public async Task GetFavouritesWithEventsAsync_ReturnsOnlyApprovedEventsOrderedByStart()
    {
        var category = SeedCategory("Workshop", "workshop", 1);
        var userId = Guid.NewGuid();
        var later = SeedEvent(category.Id, userId, EventStatus.Approved, _clock.GetCurrentInstant(), startAt: Instant.FromUtc(2026, 7, 2, 10, 0));
        var earlier = SeedEvent(category.Id, userId, EventStatus.Approved, _clock.GetCurrentInstant(), startAt: Instant.FromUtc(2026, 7, 1, 10, 0));
        var pending = SeedEvent(category.Id, userId, EventStatus.Pending, _clock.GetCurrentInstant(), startAt: Instant.FromUtc(2026, 7, 3, 10, 0));
        await _db.EventFavourites.AddRangeAsync(
            BuildFavourite(userId, later.Id),
            BuildFavourite(userId, earlier.Id),
            BuildFavourite(userId, pending.Id));
        await _db.SaveChangesAsync();

        var result = await _repo.GetFavouritesWithEventsAsync(userId);

        result.Select(f => f.GuideEventId).Should().Equal(earlier.Id, later.Id);
    }

    [HumansFact]
    public async Task GetActiveCampEventsAsync_ReturnsOnlyPendingAndApprovedCampEvents()
    {
        var category = SeedCategory("Workshop", "workshop", 1);
        var pendingCamp = SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, _clock.GetCurrentInstant(), campId: Guid.NewGuid());
        var approvedCamp = SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Approved, _clock.GetCurrentInstant(), campId: Guid.NewGuid());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Rejected, _clock.GetCurrentInstant(), campId: Guid.NewGuid());
        SeedEvent(category.Id, Guid.NewGuid(), EventStatus.Pending, _clock.GetCurrentInstant());
        await _db.SaveChangesAsync();

        var result = await _repo.GetActiveCampEventsAsync();

        result.Select(e => e.Id).Should().BeEquivalentTo([pendingCamp.Id, approvedCamp.Id]);
        result.Should().OnlyContain(e => e.CampId.HasValue);
    }

    private EventCategory SeedCategory(string name, string slug, int displayOrder, bool isActive = true)
    {
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            DisplayOrder = displayOrder,
            IsActive = isActive
        };
        _db.EventCategories.Add(category);
        return category;
    }

    private EventVenue SeedVenue(string name, int displayOrder)
    {
        var venue = new EventVenue
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayOrder = displayOrder
        };
        _db.EventVenues.Add(venue);
        return venue;
    }

    private Event SeedEvent(
        Guid categoryId,
        Guid submitterUserId,
        EventStatus status,
        Instant submittedAt,
        Instant? startAt = null,
        Guid? campId = null,
        Guid? venueId = null)
    {
        EnsureUser(submitterUserId);
        if (campId.HasValue)
        {
            EnsureCamp(campId.Value, submitterUserId);
        }

        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            CategoryId = categoryId,
            SubmitterUserId = submitterUserId,
            Title = $"Event {Guid.NewGuid():N}",
            Description = "Test description",
            StartAt = startAt ?? Instant.FromUtc(2026, 7, 1, 10, 0),
            DurationMinutes = 60,
            PriorityRank = 1,
            Status = status,
            SubmittedAt = submittedAt,
            LastUpdatedAt = submittedAt,
            CampId = campId,
            GuideSharedVenueId = venueId
        };
        _db.Events.Add(guideEvent);
        return guideEvent;
    }

    private void EnsureUser(Guid userId)
    {
        if (_db.Users.Local.Any(u => u.Id == userId))
        {
            return;
        }

        _db.Users.Add(new User
        {
            Id = userId,
            DisplayName = $"User {userId:N}",
            CreatedAt = _clock.GetCurrentInstant()
        });
    }

    private void EnsureCamp(Guid campId, Guid createdByUserId)
    {
        if (_db.Camps.Local.Any(c => c.Id == campId))
        {
            return;
        }

        _db.Camps.Add(new Camp
        {
            Id = campId,
            Slug = $"camp-{campId:N}",
            ContactEmail = "camp@example.test",
            ContactPhone = "+34000000000",
            CreatedByUserId = createdByUserId,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
    }

    private EventFavourite BuildFavourite(Guid userId, Guid guideEventId)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            GuideEventId = guideEventId,
            CreatedAt = _clock.GetCurrentInstant()
        };
}

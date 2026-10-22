using AwesomeAssertions;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Events.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;
using Humans.Events.Domain;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Writes and reads through the DI-resolved Singleton <see cref="CachingEventService"/>: a decorator that forgets to
/// invalidate after a write is a silent bug the architecture ratchets can't see.
/// </summary>
public class CachingEventServiceTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact]
    public async Task CreateCategoryAsync_through_decorator_is_visible_to_next_read()
    {
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        svc.Should().BeOfType<CachingEventService>(
            "the unkeyed IEventManagement registration must resolve to the Singleton decorator");

        // Warm first so the read hits the cache, not a lazy load.
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var slug = $"itest-cat-{Guid.NewGuid():N}";
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Integration test category",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(TestContext.Current.CancellationToken),
            IsActive = true,
        };

        await svc.CreateCategoryAsync(category, TestContext.Current.CancellationToken);

        // A write that didn't invalidate (or an invalidator wired to another instance) leaves the new row out.
        var active = await svc.GetActiveCategoriesAsync(TestContext.Current.CancellationToken);
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Integration test category");
    }

    [HumansFact]
    public async Task UpdateCategoryAsync_through_decorator_updates_cached_projection()
    {
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var slug = $"itest-rename-{Guid.NewGuid():N}";
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Original name",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(TestContext.Current.CancellationToken),
            IsActive = true,
        };
        await svc.CreateCategoryAsync(category, TestContext.Current.CancellationToken);

        category.Name = "Renamed via decorator";
        await svc.UpdateCategoryAsync(category, TestContext.Current.CancellationToken);

        // The rename must refresh the category list and the approved-events projection's flattened CategoryName.
        var active = await svc.GetActiveCategoriesAsync(TestContext.Current.CancellationToken);
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Renamed via decorator");
    }

    [HumansFact]
    public async Task GetCategoryAsync_returns_inactive_category_for_admin_edit()
    {
        // GetCategoryAsync must see inactive rows, or EditCategory breaks for them: the IsActive filter belongs in
        // GetActive*Async, not the snapshot.
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var slug = $"itest-inactive-cat-{Guid.NewGuid():N}";
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Inactive cat",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(TestContext.Current.CancellationToken),
            IsActive = false,
        };
        await svc.CreateCategoryAsync(category, TestContext.Current.CancellationToken);

        var roundTrip = await svc.GetCategoryAsync(category.Id, TestContext.Current.CancellationToken);
        roundTrip.Should().NotBeNull("admin EditCategory must resolve inactive rows by id");
        roundTrip.Slug.Should().Be(slug);
        roundTrip.IsActive.Should().BeFalse();

        // And the active-only projection must still exclude it.
        var active = await svc.GetActiveCategoriesAsync(TestContext.Current.CancellationToken);
        active.Should().NotContain(c => c.Id == category.Id);
    }

    [HumansFact]
    public async Task GetVenueAsync_returns_inactive_venue_for_admin_edit()
    {
        // Same for venues, or EditVenue 404s.
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var name = $"itest-inactive-venue-{Guid.NewGuid():N}";
        var venue = new EventVenue
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayOrder = await svc.GetNextVenueOrderAsync(TestContext.Current.CancellationToken),
            IsActive = false,
        };
        await svc.CreateVenueAsync(venue, TestContext.Current.CancellationToken);

        var roundTrip = await svc.GetVenueAsync(venue.Id, TestContext.Current.CancellationToken);
        roundTrip.Should().NotBeNull("admin EditVenue must resolve inactive rows by id");
        roundTrip.Name.Should().Be(name);
        roundTrip.IsActive.Should().BeFalse();

        var active = await svc.GetActiveVenuesAsync(TestContext.Current.CancellationToken);
        active.Should().NotContain(v => v.Id == venue.Id);
    }

    [HumansFact]
    public async Task AdminUpdateAsync_through_decorator_updates_cached_approved_event()
    {
        // An Approved event edited via AdminUpdateAsync stays approved, and the cached projection shows the new values.
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Edit-cache cat",
            Slug = $"itest-edit-{Guid.NewGuid():N}",
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(TestContext.Current.CancellationToken),
            IsActive = true,
        };
        await svc.CreateCategoryAsync(category, TestContext.Current.CancellationToken);

        var venue = new EventVenue
        {
            Id = Guid.NewGuid(),
            Name = $"itest-edit-venue-{Guid.NewGuid():N}",
            DisplayOrder = await svc.GetNextVenueOrderAsync(TestContext.Current.CancellationToken),
            IsActive = true,
        };
        await svc.CreateVenueAsync(venue, TestContext.Current.CancellationToken);

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            CampId = null,
            GuideSharedVenueId = venue.Id,
            SubmitterUserId = Guid.NewGuid(),
            CategoryId = category.Id,
            Title = "Original title",
            Description = "Desc",
            StartAt = Instant.FromUtc(2026, 7, 8, 18, 0),
            DurationMinutes = 60,
            Status = EventStatus.Pending,
            SubmittedAt = Instant.FromUtc(2026, 7, 1, 0, 0),
            LastUpdatedAt = Instant.FromUtc(2026, 7, 1, 0, 0),
        };
        await svc.SubmitEventAsync(ev, lifecycleActionUrl: null, TestContext.Current.CancellationToken);
        await svc.ApplyModerationAsync(ev.Id, Guid.NewGuid(), EventModerationActionType.Approved, null, submitterEditUrl: null, TestContext.Current.CancellationToken);

        (await svc.GetApprovedEventByIdAsync(ev.Id, TestContext.Current.CancellationToken))!.Title.Should().Be("Original title");

        // Mirror the controller: send the edited fields by id; the service edits in place.
        var edit = new EventEdit(
            ev.CategoryId, ev.GuideSharedVenueId, "Edited title", ev.Description, ev.StartAt, ev.DurationMinutes,
            ev.LocationNote, ev.Host, ev.IsRecurring, ev.RecurrenceDays, ev.PriorityRank);
        await svc.AdminUpdateAsync(ev.Id, edit, Guid.NewGuid(), "fixed typo", TestContext.Current.CancellationToken);

        var loaded = await svc.GetEventForModerationAsync(ev.Id, TestContext.Current.CancellationToken);
        loaded!.Status.Should().Be(EventStatus.Approved);
        var cached = await svc.GetApprovedEventByIdAsync(ev.Id, TestContext.Current.CancellationToken);
        cached.Should().NotBeNull("an edited Approved event stays approved and in the cache");
        cached.Title.Should().Be("Edited title");
    }

    [HumansFact]
    public async Task CategorySlugExistsAsync_detects_collision_with_inactive_row()
    {
        // The unique index covers inactive rows too, so the cache check must, or INSERT throws DbUpdateException instead
        // of a friendly ModelState message.
        var svc = Factory.Services.GetRequiredService<IEventManagement>();
        await ((CachingEventService)svc).WarmAllAsync(TestContext.Current.CancellationToken);

        var slug = $"itest-slug-collide-{Guid.NewGuid():N}";
        var inactive = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Owns the slug",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(TestContext.Current.CancellationToken),
            IsActive = false,
        };
        await svc.CreateCategoryAsync(inactive, TestContext.Current.CancellationToken);

        var collides = await svc.CategorySlugExistsAsync(slug, ct: TestContext.Current.CancellationToken);
        collides.Should().BeTrue("slug uniqueness must consider inactive rows to match the DB unique index");

        // ExcludeId path still works.
        var collidesExcludingSelf = await svc.CategorySlugExistsAsync(slug, inactive.Id, TestContext.Current.CancellationToken);
        collidesExcludingSelf.Should().BeFalse();
    }
}

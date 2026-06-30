using AwesomeAssertions;
using Humans.Application.Interfaces.Events;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Events;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Services;

/// <summary>
/// Integration tests that exercise <see cref="CachingEventService"/> end-to-end
/// through the DI-resolved <see cref="IEventService"/> surface. Closes the
/// §15g-bis "decorator forgot to invalidate after a write" silent-bug gap that
/// the architecture-only ratchets can't detect: writes go through the
/// Singleton decorator, reads come back through the same decorator, and the
/// assertion is that the cached projection reflects the write.
/// </summary>
public class CachingEventServiceTests(HumansWebApplicationFactory factory) : IClassFixture<HumansWebApplicationFactory>
{
    [HumansFact]
    public async Task CreateCategoryAsync_through_decorator_is_visible_to_next_read()
    {
        var svc = factory.Services.GetRequiredService<IEventService>();
        svc.Should().BeOfType<CachingEventService>(
            "the unkeyed IEventService registration must resolve to the Singleton decorator");

        // Warm first so the read path is hitting the cache, not lazy-load.
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

        // Read back through the same decorator. If the write path forgot to
        // invalidate (or the invalidator wasn't wired to the same Singleton),
        // the cached snapshot would not contain the new row.
        var active = await svc.GetActiveCategoriesAsync(TestContext.Current.CancellationToken);
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Integration test category");
    }

    [HumansFact]
    public async Task UpdateCategoryAsync_through_decorator_updates_cached_projection()
    {
        var svc = factory.Services.GetRequiredService<IEventService>();
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

        // Mutate and update through the decorator.
        category.Name = "Renamed via decorator";
        await svc.UpdateCategoryAsync(category, TestContext.Current.CancellationToken);

        // The decorator must have refreshed both the category list AND the
        // approved-events projection (flattened CategoryName). At minimum the
        // category list should reflect the rename on the next read.
        var active = await svc.GetActiveCategoriesAsync(TestContext.Current.CancellationToken);
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Renamed via decorator");
    }

    [HumansFact]
    public async Task GetCategoryAsync_returns_inactive_category_for_admin_edit()
    {
        // Codex P1 (PR #582): GetCategoryAsync served from an active-only
        // snapshot, breaking EditCategory for inactive rows. Snapshot must
        // hold all rows; IsActive filter belongs in GetActive*Async.
        var svc = factory.Services.GetRequiredService<IEventService>();
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
        // Codex P1 (PR #582): same regression on venues — EditVenue 404s.
        var svc = factory.Services.GetRequiredService<IEventService>();
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
        // §15g-bis for the admin in-place edit path: an Approved event edited
        // via AdminUpdateAsync stays approved (status preserved) and the cached
        // approved-events projection must reflect the new field values.
        var svc = factory.Services.GetRequiredService<IEventService>();
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

        // Mirror the controller: re-load fresh, mutate, save in place.
        var loaded = await svc.GetEventForModerationAsync(ev.Id, TestContext.Current.CancellationToken);
        loaded!.Title = "Edited title";
        await svc.AdminUpdateAsync(loaded, Guid.NewGuid(), "fixed typo", TestContext.Current.CancellationToken);

        loaded.Status.Should().Be(EventStatus.Approved);
        var cached = await svc.GetApprovedEventByIdAsync(ev.Id, TestContext.Current.CancellationToken);
        cached.Should().NotBeNull("an edited Approved event stays approved and in the cache");
        cached.Title.Should().Be("Edited title");
    }

    [HumansFact]
    public async Task CategorySlugExistsAsync_detects_collision_with_inactive_row()
    {
        // Codex P1 (PR #582): DB unique index covers active+inactive rows;
        // the cache check must match or INSERT will throw DbUpdateException
        // instead of producing a friendly ModelState message.
        var svc = factory.Services.GetRequiredService<IEventService>();
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

using AwesomeAssertions;
using Humans.Application.Interfaces.Events;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Events;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
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
public class CachingEventServiceTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public CachingEventServiceTests(HumansWebApplicationFactory factory) =>
        _factory = factory;

    [HumansFact]
    public async Task CreateCategoryAsync_through_decorator_is_visible_to_next_read()
    {
        var svc = _factory.Services.GetRequiredService<IEventService>();
        svc.Should().BeOfType<CachingEventService>(
            "the unkeyed IEventService registration must resolve to the Singleton decorator");

        // Warm first so the read path is hitting the cache, not lazy-load.
        await ((CachingEventService)svc).WarmAllAsync();

        var slug = $"itest-cat-{Guid.NewGuid():N}";
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Integration test category",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(),
            IsActive = true,
        };

        await svc.CreateCategoryAsync(category);

        // Read back through the same decorator. If the write path forgot to
        // invalidate (or the invalidator wasn't wired to the same Singleton),
        // the cached snapshot would not contain the new row.
        var active = await svc.GetActiveCategoriesAsync();
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Integration test category");
    }

    [HumansFact]
    public async Task UpdateCategoryAsync_through_decorator_updates_cached_projection()
    {
        var svc = _factory.Services.GetRequiredService<IEventService>();
        await ((CachingEventService)svc).WarmAllAsync();

        var slug = $"itest-rename-{Guid.NewGuid():N}";
        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = "Original name",
            Slug = slug,
            IsSensitive = false,
            DisplayOrder = await svc.GetNextCategoryOrderAsync(),
            IsActive = true,
        };
        await svc.CreateCategoryAsync(category);

        // Mutate and update through the decorator.
        category.Name = "Renamed via decorator";
        await svc.UpdateCategoryAsync(category);

        // The decorator must have refreshed both the category list AND the
        // approved-events projection (flattened CategoryName). At minimum the
        // category list should reflect the rename on the next read.
        var active = await svc.GetActiveCategoriesAsync();
        active.Should().ContainSingle(c => c.Slug == slug)
            .Which.Name.Should().Be("Renamed via decorator");
    }
}

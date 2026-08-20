using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Events.Contracts;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Events.Tests.Services;

/// <summary>
/// Covers <see cref="CachingEventService.SearchAsync"/> — Events' scored search hit
/// (nobodies-collective/Humans#1062). Served entirely from the approved-event cache warmed
/// off the substitute inner <see cref="IEventService"/>; no DB involved.
/// </summary>
public sealed class CachingEventServiceTests
{
    private readonly IEventService _inner = Substitute.For<IEventService>();
    private readonly CachingEventService _service;

    public CachingEventServiceTests()
    {
        _inner.GetAllCategoriesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventCategoryManageInfo>>([]));
        _inner.GetAllVenuesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventVenueManageInfo>>([]));
        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventGuideSettingsView?>(null));

        var services = new ServiceCollection();
        services.AddKeyedScoped<IEventService>(
            CachingEventService.InnerServiceKey, (_, _) => _inner);
        var provider = services.BuildServiceProvider();

        _service = new CachingEventService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingEventService>.Instance);
    }

    private void SeedApproved(params ApprovedEventView[] events) =>
        _inner.GetApprovedEventsAsync(
                null, null, null, null, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApprovedEventView>>(events));

    [HumansFact]
    public async Task SearchAsync_TitleExactMatch_ScoresTheExactTier()
    {
        SeedApproved(Approved("Sunset Yoga"));

        var hits = await _service.SearchAsync("Sunset Yoga", 10, TestContext.Current.CancellationToken);

        hits.Should().ContainSingle().Which.Score.Should().Be(StringSearchExtensions.ExactNameScore);
    }

    [HumansFact]
    public async Task SearchAsync_TitlePrefixMatch_ScoresThePrefixTier()
    {
        SeedApproved(Approved("Sunset Yoga Session"));

        var hits = await _service.SearchAsync("Sunset", 10, TestContext.Current.CancellationToken);

        hits.Should().ContainSingle().Which.Score.Should().Be(StringSearchExtensions.PrefixNameScore);
    }

    [HumansFact]
    public async Task SearchAsync_TitleContainsMatch_ScoresTheContainsTier()
    {
        SeedApproved(Approved("Morning Sunset Yoga"));

        var hits = await _service.SearchAsync("Sunset", 10, TestContext.Current.CancellationToken);

        hits.Should().ContainSingle().Which.Score.Should().Be(StringSearchExtensions.ContainsNameScore);
    }

    [HumansFact]
    public async Task SearchAsync_DescriptionOnlyMatch_StillAppears_RankedBelowEveryTitleMatch()
    {
        // Title misses entirely; only the description carries the match. It must still
        // surface, and its score must stay below the lowest title tier (Contains, 60) so a
        // description-only hit never outranks a real title match.
        SeedApproved(
            Approved("Meditation Circle", description: "Guided breathing for beginners"),
            Approved("Sunrise Hike", description: "A gentle Sunset walk up the ridge"));

        var hits = await _service.SearchAsync("Sunset", 10, TestContext.Current.CancellationToken);

        var hit = hits.Should().ContainSingle().Subject;
        hit.Title.Should().Be("Sunrise Hike");
        hit.Score.Should().BeLessThan(StringSearchExtensions.ContainsNameScore).And.BeGreaterThan(0);
    }

    [HumansFact]
    public async Task SearchAsync_DescriptionOnlyMatches_TierAmongThemselves_ExactAboveContains()
    {
        SeedApproved(
            Approved("Alpha", description: "Sunset"), // description exact match
            Approved("Beta", description: "A calm Sunset walk")); // description contains match

        var hits = await _service.SearchAsync("Sunset", 10, TestContext.Current.CancellationToken);

        hits.Should().HaveCount(2);
        var exact = hits.Single(h => string.Equals(h.Title, "Alpha", StringComparison.Ordinal)).Score;
        var contains = hits.Single(h => string.Equals(h.Title, "Beta", StringComparison.Ordinal)).Score;
        exact.Should().BeGreaterThan(contains);
    }

    [HumansFact]
    public async Task SearchAsync_HonoursTheLimit()
    {
        SeedApproved(
            Approved("Alpha Sunset"),
            Approved("Beta Sunset"),
            Approved("Gamma Sunset"));

        var hits = await _service.SearchAsync("Sunset", 2, TestContext.Current.CancellationToken);

        hits.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_JumpsStraightToThatEvent()
    {
        var wanted = Approved("Anything");
        SeedApproved(wanted, Approved("Something Else"));

        var hits = await _service.SearchAsync(wanted.Id.ToString(), 10, TestContext.Current.CancellationToken);

        var hit = hits.Should().ContainSingle().Subject;
        hit.EventId.Should().Be(wanted.Id);
        hit.Score.Should().Be(StringSearchExtensions.ExactNameScore);
    }

    private static ApprovedEventView Approved(string title, string description = "Anything at all") => new(
        Id: Guid.NewGuid(), CampId: null, GuideSharedVenueId: null, SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: title, Description: description, LocationNote: null, Host: null,
        StartAt: Instant.FromUtc(2026, 8, 1, 10, 0), DurationMinutes: 60, IsRecurring: false, RecurrenceDays: null,
        PriorityRank: 0, SubmittedAt: Instant.FromUtc(2026, 8, 1, 10, 0), LastUpdatedAt: Instant.FromUtc(2026, 8, 1, 10, 0));
}

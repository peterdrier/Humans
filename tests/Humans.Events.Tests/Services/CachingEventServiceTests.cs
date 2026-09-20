using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Events.Contracts;
using Humans.Events.Services;
using Humans.Events.Services.Dtos;
using Humans.Settings.Contracts;
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

    [HumansFact]
    public async Task EraseForUserAsync_RefreshesTheCachedEvents()
    {
        // The submitted event stays in the guide with its Host name cleared, so
        // erasure edits a row the cache is already serving.
        var before = Approved("Sunset Yoga") with { Host = "Ada" };
        SeedApproved(before);
        (await _service.GetApprovedEventByIdAsync(before.Id, TestContext.Current.CancellationToken))!
            .Host.Should().Be("Ada");

        SeedApproved(before with { Host = null });
        await _service.EraseForUserAsync(before.SubmitterUserId, TestContext.Current.CancellationToken);

        await _inner.Received(1).EraseForUserAsync(before.SubmitterUserId, Arg.Any<CancellationToken>());
        (await _service.GetApprovedEventByIdAsync(before.Id, TestContext.Current.CancellationToken))!
            .Host.Should().BeNull();
    }

    private static ApprovedEventView Approved(string title, string description = "Anything at all") => new(
        Id: Guid.NewGuid(), CampId: null, GuideSharedVenueId: null, SubmitterUserId: Guid.NewGuid(),
        CategoryId: Guid.NewGuid(), CategorySlug: "music", CategoryName: "Music", CategoryIsSensitive: false,
        VenueName: null, Title: title, Description: description, LocationNote: null, Host: null,
        StartAt: Instant.FromUtc(2026, 8, 1, 10, 0), DurationMinutes: 60, IsRecurring: false, RecurrenceDays: null,
        PriorityRank: 0, SubmittedAt: Instant.FromUtc(2026, 8, 1, 10, 0), LastUpdatedAt: Instant.FromUtc(2026, 8, 1, 10, 0));

    [HumansFact]
    public async Task EventSettingsChanged_ReloadsTheGuideSettingsProjection()
    {
        // The projection carries TimeZoneId stitched from the Settings-owned event settings
        // row, and the guide renders every event time in it. Only Events' own writes used to
        // refresh it, so a timezone edit on the Settings tab showed the old zone until restart.
        var before = GuideSettings("Europe/Madrid");
        var after = GuideSettings("Atlantic/Canary");
        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventGuideSettingsView?>(before));

        var first = await _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken);
        first!.TimeZoneId.Should().Be("Europe/Madrid");

        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventGuideSettingsView?>(after));

        // Without the listener the cached projection is served forever.
        var stale = await _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken);
        stale!.TimeZoneId.Should().Be("Europe/Madrid");

        ((IEventSettingsChangeListener)_service).EventSettingsChanged(Guid.NewGuid());

        var fresh = await _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken);
        fresh!.TimeZoneId.Should().Be("Atlantic/Canary");
    }

    [HumansFact]
    public async Task EventSettingsChanged_FailedRefreshKeepsTheProjectionStale()
    {
        // If the first read after a change fails (cancelled request, database error) the
        // stale marker must survive it, or the old timezone is served until restart.
        var before = GuideSettings("Europe/Madrid");
        var after = GuideSettings("Atlantic/Canary");
        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventGuideSettingsView?>(before));
        (await _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken))!
            .TimeZoneId.Should().Be("Europe/Madrid");

        ((IEventSettingsChangeListener)_service).EventSettingsChanged(Guid.NewGuid());
        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns<EventGuideSettingsView?>(_ => throw new OperationCanceledException());

        var failed = () => _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken);
        await failed.Should().ThrowAsync<OperationCanceledException>();

        _inner.GetGuideSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventGuideSettingsView?>(after));

        var fresh = await _service.GetGuideSettingsAsync(TestContext.Current.CancellationToken);
        fresh!.TimeZoneId.Should().Be("Atlantic/Canary");
    }

    private static EventGuideSettingsView GuideSettings(string timeZoneId) => new(
        Id: Guid.NewGuid(),
        EventSettingsId: Guid.NewGuid(),
        SubmissionOpenAt: Instant.FromUtc(2026, 1, 1, 0, 0),
        SubmissionCloseAt: Instant.FromUtc(2026, 2, 1, 0, 0),
        GuidePublishAt: Instant.FromUtc(2026, 3, 1, 0, 0),
        MaxPrintSlots: 3,
        TimeZoneId: timeZoneId,
        CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
}

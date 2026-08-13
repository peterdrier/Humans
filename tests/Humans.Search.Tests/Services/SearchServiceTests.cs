using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Search.Services;
using Humans.Search.Services.Dtos;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Search.Tests.Services;

/// <summary>
/// Orchestration tests for <see cref="SearchService"/> against substitutes for the five
/// section read interfaces it fans out to. Two things are pinned here that no other test
/// can see: the field mask the human bucket asks for (<see cref="PersonSearchFields.PublicAll"/>
/// — the only search-time privacy filter this service owns), and the GUID short-circuit that
/// scores an id paste as an exact match.
///
/// <para>
/// Per the 2026-08-07 ruling on nobodies-collective/Humans#985, search is not an
/// authorization boundary: a hit is a routing convenience and the destination page enforces
/// visibility. The per-section text-query filters that this service depends on are pinned
/// where they live — <c>CachingTeamServiceTests</c>, <c>CachingCampServiceTests</c>,
/// <c>ShiftManagementServiceTests</c> and <c>ShiftRepositoryRotaSearchVisibilityTests</c>.
/// </para>
/// </summary>
public sealed class SearchServiceTests
{
    private const int ScoreExact = 100;
    private const int ScorePrefix = 80;
    private const int ScoreContains = 60;

    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IShiftManagementServiceRead _shifts = Substitute.For<IShiftManagementServiceRead>();
    private readonly IEventServiceRead _events = Substitute.For<IEventServiceRead>();

    public SearchServiceTests()
    {
        StubHumans(MakeHuman("Kitchen Sink"));
        StubTeams(new TeamSearchHit("Kitchen", "kitchen"));
        StubCamps(new CampSearchHit("kitchen-camp", "Kitchen"));
        StubRotas(new RotaSearchHit("Kitchen", Guid.NewGuid(), "Cantina"));
        StubEvents(MakeEvent("Kitchen Takeover", "Food"));
    }

    // ==========================================================================
    // Query-length gate
    // ==========================================================================

    [HumansTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData("  k  ")]
    public async Task SearchAsync_QueryShorterThanTwoCharsAfterTrim_ReturnsEmpty_AndTouchesNoSection(string query)
    {
        var results = await Build().SearchAsync(query, ct: TestContext.Current.CancellationToken);

        results.Query.Should().Be(query.Trim());
        results.Humans.Should().BeEmpty();
        results.Teams.Should().BeEmpty();
        results.Camps.Should().BeEmpty();
        results.Shifts.Should().BeEmpty();
        results.Events.Should().BeEmpty();

        await _users.DidNotReceive().SearchUsersAsync(
            Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _teams.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _camps.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _shifts.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_TwoCharQuery_IsAtTheGateBoundary_AndFansOut()
    {
        await Build().SearchAsync("ki", ct: TestContext.Current.CancellationToken);

        await _teams.Received(1).SearchAsync("ki", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_TrimsQuery_BeforeFanOut_AndEchoesTheTrimmedForm()
    {
        var results = await Build().SearchAsync("  Kitchen  ", ct: TestContext.Current.CancellationToken);

        results.Query.Should().Be("Kitchen");
        await _teams.Received(1).SearchAsync("Kitchen", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _camps.Received(1).SearchAsync("Kitchen", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _shifts.Received(1).SearchAsync("Kitchen", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // onlyType short-circuit
    // ==========================================================================

    /// <summary>
    /// Each <c>onlyType</c> value queries just that section's source and skips the other four.
    /// </summary>
    /// <remarks>
    /// One <c>[Fact]</c> over the five cases rather than a <c>[HumansTheory]</c> with five
    /// <c>[InlineData]</c>: <c>SearchResultType</c> turned <c>internal</c> at the G5 move, and a
    /// <c>public</c> test method cannot take an internal parameter (CS0051) even with
    /// <c>InternalsVisibleTo</c>. Each case clears the substitutes' received calls first, which
    /// is what the per-case theory instance used to give for free
    /// (G5-SECTION-TEMPLATE.md step 8, Issues' rule).
    /// </remarks>
    [HumansFact]
    public async Task SearchAsync_OnlyType_QueriesThatSectionAndSkipsTheOtherFour()
    {
        foreach (var onlyType in Enum.GetValues<SearchResultType>())
        {
            await AssertOnlyTypeQueriesOneSection(onlyType);
        }
    }

    private async Task AssertOnlyTypeQueriesOneSection(SearchResultType onlyType)
    {
        _users.ClearReceivedCalls();
        _teams.ClearReceivedCalls();
        _camps.ClearReceivedCalls();
        _shifts.ClearReceivedCalls();
        _events.ClearReceivedCalls();

        var results = await Build().SearchAsync("Kitchen", onlyType, TestContext.Current.CancellationToken);

        results.Humans.Should().HaveCount(onlyType == SearchResultType.Human ? 1 : 0);
        results.Teams.Should().HaveCount(onlyType == SearchResultType.Team ? 1 : 0);
        results.Camps.Should().HaveCount(onlyType == SearchResultType.Camp ? 1 : 0);
        results.Shifts.Should().HaveCount(onlyType == SearchResultType.Shift ? 1 : 0);
        results.Events.Should().HaveCount(onlyType == SearchResultType.Event ? 1 : 0);

        await _users.Received(onlyType == SearchResultType.Human ? 1 : 0).SearchUsersAsync(
            Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _teams.Received(onlyType == SearchResultType.Team ? 1 : 0).SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _camps.Received(onlyType == SearchResultType.Camp ? 1 : 0).SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _shifts.Received(onlyType == SearchResultType.Shift ? 1 : 0).SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _events.Received(onlyType == SearchResultType.Event ? 1 : 0).GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_NoOnlyType_QueriesEverySection()
    {
        var results = await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Humans.Should().ContainSingle();
        results.Teams.Should().ContainSingle();
        results.Camps.Should().ContainSingle();
        results.Shifts.Should().ContainSingle();
        results.Events.Should().ContainSingle();
    }

    // ==========================================================================
    // Score tiers and their boundaries
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_ScoresTeamNames_ExactThenPrefixThenContains_AndDropsNonMatches()
    {
        StubTeams(
            new TeamSearchHit("Kitchen", "kitchen"),
            new TeamSearchHit("Kitchen Crew", "kitchen-crew"),
            new TeamSearchHit("Main Kitchen", "main-kitchen"),
            new TeamSearchHit("Gate", "gate"));

        var results = await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Teams.Should().SatisfyRespectively(
            exact => exact.Score.Should().Be(ScoreExact),
            prefix => prefix.Score.Should().Be(ScorePrefix),
            contains => contains.Score.Should().Be(ScoreContains));
        results.Teams.Should().NotContain(r => r.Title == "Gate");
    }

    [HumansFact]
    public async Task SearchAsync_ScoringIsCaseInsensitive_SoCasingNeverDemotesAnExactMatch()
    {
        StubTeams(new TeamSearchHit("KITCHEN", "kitchen"));

        var results = await Build().SearchAsync("kitchen", ct: TestContext.Current.CancellationToken);

        results.Teams.Should().ContainSingle().Which.Score.Should().Be(ScoreExact);
    }

    [HumansFact]
    public async Task SearchAsync_EmptyName_ScoresZero_AndIsDropped()
    {
        StubTeams(new TeamSearchHit(string.Empty, "nameless"));

        var results = await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Teams.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchAsync_ScoresAndBuildsUrls_ForCampAndRotaBucketsToo()
    {
        var teamId = Guid.NewGuid();
        StubCamps(
            new CampSearchHit("garden-of-joy", "Garden of Joy"),
            new CampSearchHit("gate-camp", "Gate"));
        StubRotas(
            new RotaSearchHit("Garden", teamId, "Gardening"),
            new RotaSearchHit("Perimeter", teamId, "Gate"));

        var results = await Build().SearchAsync("Garden", ct: TestContext.Current.CancellationToken);

        var camp = results.Camps.Should().ContainSingle().Subject;
        camp.Score.Should().Be(ScorePrefix);
        camp.Url.Should().Be("/Camps/garden-of-joy");
        camp.Subtitle.Should().Be("garden-of-joy");

        var rota = results.Shifts.Should().ContainSingle().Subject;
        rota.Score.Should().Be(ScoreExact);
        rota.Url.Should().Be($"/Shifts?departmentId={teamId}");
        rota.Subtitle.Should().Be("Gardening");
    }

    // ==========================================================================
    // The ruling: a GUID hit is a routing convenience, not an authorization statement
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ReturnsTheTeamCampAndRotaHits_EvenThoughTheNameCannotMatch()
    {
        // Ruling (nobodies-collective/Humans#985, 2026-08-07): by-GUID lookups skip the
        // visibility filter. The caller already holds the id; the destination page decides
        // whether they may open it. Nothing here depends on the viewer's role — the service
        // takes no viewer at all, so the hit comes back for admin and non-admin alike.
        var id = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        StubTeams(new TeamSearchHit("Hidden Ops", "hidden-ops"));
        StubCamps(new CampSearchHit("pending-camp", "Pending Camp"));
        StubRotas(new RotaSearchHit("Admin Only Rota", teamId, "Gate"));

        var results = await Build().SearchAsync(id.ToString(), ct: TestContext.Current.CancellationToken);

        results.Teams.Should().ContainSingle().Which.Score.Should().Be(ScoreExact);
        results.Camps.Should().ContainSingle().Which.Score.Should().Be(ScoreExact);
        results.Shifts.Should().ContainSingle().Which.Score.Should().Be(ScoreExact);
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_StillReturnsNothing_WhenTheSectionResolvedNoEntity()
    {
        StubTeams();
        StubCamps();
        StubRotas();

        var results = await Build().SearchAsync(
            Guid.NewGuid().ToString(), ct: TestContext.Current.CancellationToken);

        results.Teams.Should().BeEmpty();
        results.Camps.Should().BeEmpty();
        results.Shifts.Should().BeEmpty();
    }

    // ==========================================================================
    // The one search-time privacy filter this service owns
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_HumanBucket_AlwaysAsksForPublicFieldsOnly()
    {
        // The only privacy guarantee that survives the ruling at *search* time: admin-only
        // profile fields (verified emails, non-public contact fields, legal names) are never
        // matched, for anyone. SearchService takes no viewer, so "regardless of role" is
        // structural — the mask is a constant, and this asserts the constant.
        await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        await _users.Received(1).SearchUsersAsync(
            "Kitchen", PersonSearchFields.PublicAll, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _users.DidNotReceive().SearchUsersAsync(
            Arg.Any<string>(),
            Arg.Is<PersonSearchFields>(f =>
                f.HasFlag(PersonSearchFields.Admin) || f.HasFlag(PersonSearchFields.LegalName)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_AlsoAsksForPublicFieldsOnly()
    {
        // The id path must not widen the field mask on the way. It skips only the mask:
        // CachingUserService.SearchUsersAsync still requires a non-rejected profile on its
        // GUID branch, so eligibility is unchanged — that gate is covered where it lives,
        // not here, since this harness stubs IUserServiceRead.
        await Build().SearchAsync(Guid.NewGuid().ToString(), ct: TestContext.Current.CancellationToken);

        await _users.Received(1).SearchUsersAsync(
            Arg.Any<string>(), PersonSearchFields.PublicAll, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_PassesAnUnboundedCap_ToEverySection()
    {
        // No per-type cap at ~500-user scale: capping made people miss matches.
        await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        await _users.Received(1).SearchUsersAsync(
            Arg.Any<string>(), Arg.Any<PersonSearchFields>(), int.MaxValue, Arg.Any<CancellationToken>());
        await _teams.Received(1).SearchAsync(Arg.Any<string>(), int.MaxValue, Arg.Any<CancellationToken>());
        await _camps.Received(1).SearchAsync(Arg.Any<string>(), int.MaxValue, Arg.Any<CancellationToken>());
        await _shifts.Received(1).SearchAsync(Arg.Any<string>(), int.MaxValue, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Features:Events flag
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_EventsFeatureOff_ReturnsNoEvents_AndNeverCallsTheEventsSection()
    {
        var results = await Build(eventsEnabled: false)
            .SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Events.Should().BeEmpty();
        await _events.DidNotReceive().GetApprovedEventsAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SearchAsync_EventTitleMisses_FallsBackToTheContainsTier_SoDescriptionMatchesStillSurface()
    {
        // The Events bucket is filtered server-side on title OR description; a row that only
        // matched the description scores 0 on Title and must not be dropped like a team would be.
        StubEvents(
            MakeEvent("Sunset Yoga", "Yoga"),
            MakeEvent("Meditation Circle", "Wellbeing"));

        var results = await Build().SearchAsync("Meditation", ct: TestContext.Current.CancellationToken);

        results.Events.Should().SatisfyRespectively(
            titleMatch =>
            {
                titleMatch.Title.Should().Be("Meditation Circle");
                titleMatch.Score.Should().Be(ScorePrefix);
            },
            descriptionOnly =>
            {
                descriptionOnly.Title.Should().Be("Sunset Yoga");
                descriptionOnly.Score.Should().Be(ScoreContains);
            });
    }

    [HumansFact]
    public async Task SearchAsync_EventBucket_BuildsABrowseUrlEscapedForTheTitle()
    {
        StubEvents(MakeEvent("Fire & Ice", "Performance"));

        var results = await Build().SearchAsync("Fire", ct: TestContext.Current.CancellationToken);

        var hit = results.Events.Should().ContainSingle().Subject;
        hit.Url.Should().Be("/Events/Browse?q=Fire%20%26%20Ice");
        hit.Subtitle.Should().Be("Performance");
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private SearchService Build(bool eventsEnabled = true) =>
        new(_users, _teams, _camps, _shifts, _events,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Features:Events"] = eventsEnabled ? "true" : "false"
                })
                .Build());

    private void StubHumans(params HumanSearchResult[] hits) =>
        _users.SearchUsersAsync(
                Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HumanSearchResult>>(hits));

    private void StubTeams(params TeamSearchHit[] hits) =>
        _teams.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TeamSearchHit>>(hits));

    private void StubCamps(params CampSearchHit[] hits) =>
        _camps.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampSearchHit>>(hits));

    private void StubRotas(params RotaSearchHit[] hits) =>
        _shifts.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RotaSearchHit>>(hits));

    private void StubEvents(params ApprovedEventView[] hits) =>
        _events.GetApprovedEventsAsync(
                Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ApprovedEventView>>(hits));

    private static HumanSearchResult MakeHuman(string burnerName) =>
        new(Guid.NewGuid(), Guid.NewGuid(), burnerName, null, "Name", null, null, ScoreExact);

    private static ApprovedEventView MakeEvent(string title, string categoryName) =>
        new(
            Guid.NewGuid(),
            CampId: null,
            GuideSharedVenueId: null,
            SubmitterUserId: Guid.NewGuid(),
            CategoryId: Guid.NewGuid(),
            CategorySlug: categoryName.ToLowerInvariant(),
            categoryName,
            CategoryIsSensitive: false,
            VenueName: null,
            title,
            Description: "Anything at all",
            LocationNote: null,
            Host: null,
            StartAt: Instant.FromUtc(2026, 7, 4, 18, 0),
            DurationMinutes: 60,
            IsRecurring: false,
            RecurrenceDays: null,
            PriorityRank: 0,
            SubmittedAt: Instant.FromUtc(2026, 6, 1, 12, 0),
            LastUpdatedAt: Instant.FromUtc(2026, 6, 1, 12, 0));
}

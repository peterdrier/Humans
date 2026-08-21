using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Search.Services;
using Humans.Search.Services.Dtos;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Humans.Search.Tests.Services;

/// <summary>
/// Orchestration tests for <see cref="SearchService"/> against substitutes for the five
/// section read interfaces it fans out to. Two things are pinned here that no other test
/// can see: the field mask the human bucket asks for (<see cref="PersonSearchFields.PublicAll"/>
/// — the only search-time privacy filter this service owns), and the key/sort-key mapping
/// that is all this section keeps of another section's row
/// (nobodies-collective/Humans#1062). Name-match scoring belongs to each section (including
/// Events, since #1062's Events follow-up) and is pinned where it lives.
///
/// <para>
/// Per the 2026-08-07 ruling on nobodies-collective/Humans#985, search is not an
/// authorization boundary: a hit is a routing convenience and the destination page enforces
/// visibility. The per-section text-query filters that this service depends on are pinned
/// where they live — <c>CachingTeamServiceTests</c>, <c>CachingCampServiceTests</c>,
/// <c>CachingEventServiceTests</c>, <c>ShiftManagementServiceTests</c> and
/// <c>ShiftRepositoryRotaSearchVisibilityTests</c>.
/// </para>
/// </summary>
public sealed class SearchServiceTests
{
    private const int ScoreExact = 100;
    private const int ScorePrefix = 80;

    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly ITeamServiceRead _teams = Substitute.For<ITeamServiceRead>();
    private readonly ICampServiceRead _camps = Substitute.For<ICampServiceRead>();
    private readonly IShiftManagementServiceRead _shifts = Substitute.For<IShiftManagementServiceRead>();
    private readonly IEventServiceRead _events = Substitute.For<IEventServiceRead>();

    public SearchServiceTests()
    {
        StubHumans(MakeHuman("Kitchen Sink"));
        StubTeams(new TeamSearchHit(Guid.NewGuid(), "Kitchen", ScoreExact));
        StubCamps(new CampSearchHit(Guid.NewGuid(), "Kitchen", ScoreExact));
        StubRotas(new RotaSearchHit(Guid.NewGuid(), "Kitchen", ScoreExact));
        StubEvents(new EventSearchHit(Guid.NewGuid(), "Kitchen Takeover", ScoreExact));
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
        await _events.Received(onlyType == SearchResultType.Event ? 1 : 0).SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
    // Keys and ordering fields — this section carries them, it no longer projects display
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_CarriesEachSectionsOwnScore_WithoutRescoring()
    {
        // Teams/Camps/Shifts/Events score their own hits (nobodies-collective/Humans#1062). A
        // score that contradicts the name proves this service takes the section's word for it.
        StubTeams(new TeamSearchHit(Guid.NewGuid(), "Nothing Like The Query", ScorePrefix));
        StubEvents(new EventSearchHit(Guid.NewGuid(), "Nothing Like The Query", ScorePrefix));

        var results = await Build().SearchAsync("Kitchen", ct: TestContext.Current.CancellationToken);

        results.Teams.Should().ContainSingle().Which.Score.Should().Be(ScorePrefix);
        results.Events.Should().ContainSingle().Which.Score.Should().Be(ScorePrefix);
    }

    [HumansFact]
    public async Task SearchAsync_PassesEachSectionsKey_AndTheNameOnlyAsASortKey()
    {
        var teamId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var rotaId = Guid.NewGuid();
        StubTeams(new TeamSearchHit(teamId, "Kitchen Crew", ScorePrefix));
        StubCamps(new CampSearchHit(campId, "Garden of Joy", ScorePrefix));
        StubRotas(new RotaSearchHit(rotaId, "Garden", ScoreExact));
        var eventId = Guid.NewGuid();
        StubEvents(new EventSearchHit(eventId, "Fire & Ice", ScorePrefix));

        var results = await Build().SearchAsync("Garden", ct: TestContext.Current.CancellationToken);

        var team = results.Teams.Should().ContainSingle().Subject;
        team.Key.Should().Be(teamId);
        team.SortKey.Should().Be("Kitchen Crew");

        var camp = results.Camps.Should().ContainSingle().Subject;
        camp.Key.Should().Be(campId);
        camp.SortKey.Should().Be("Garden of Joy");

        var rota = results.Shifts.Should().ContainSingle().Subject;
        rota.Key.Should().Be(rotaId);
        rota.SortKey.Should().Be("Garden");

        var ev = results.Events.Should().ContainSingle().Subject;
        ev.Key.Should().Be(eventId);
        ev.SortKey.Should().Be("Fire & Ice");
    }

    // ==========================================================================
    // The ruling: a GUID hit is a routing convenience, not an authorization statement
    // ==========================================================================

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ReturnsWhateverEachSectionResolved()
    {
        // Ruling (nobodies-collective/Humans#985, 2026-08-07): by-GUID lookups skip the
        // visibility filter. The caller already holds the id; the destination page decides
        // whether they may open it. Each section owns that branch and scores it exact; this
        // service passes the hits through unchanged, for admin and non-admin alike.
        var id = Guid.NewGuid();
        StubTeams(new TeamSearchHit(Guid.NewGuid(), "Hidden Ops", ScoreExact));
        StubCamps(new CampSearchHit(Guid.NewGuid(), "Pending Camp", ScoreExact));
        StubRotas(new RotaSearchHit(Guid.NewGuid(), "Admin Only Rota", ScoreExact));

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
        await _events.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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

    private void StubEvents(params EventSearchHit[] hits) =>
        _events.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EventSearchHit>>(hits));

    private static HumanSearchResult MakeHuman(string burnerName) =>
        new(Guid.NewGuid(), Guid.NewGuid(), burnerName, null, "Name", null, null, ScoreExact);
}

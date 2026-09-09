using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Camps.Tests.Services;

/// <summary>
/// Behavioral tests for <see cref="CachingCampService"/>, pinning invariants
/// the architecture tests can't cover from type inspection alone:
/// <list type="bullet">
/// <item>Per-camp invalidation rebuilds <see cref="CampSeasonInfo.EeGrantedCount"/>
///   correctly (PR #583 Codex P1 — RefreshEntryAsync used to call a GetByIdAsync
///   that omitted <c>Seasons.Members</c>, dropping the count to 0).</item>
/// <item>Cold-year requests (years outside the warm scope) fall back to the
///   inner service instead of returning an empty list (PR #583 Codex P2).</item>
/// </list>
/// </summary>
public sealed class CachingCampServiceTests : CampsTestHarness
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ICampService _innerSubstitute;
    private readonly ICampRoleCampAccess _innerRoleAccess;
    private readonly CachingCampService _service;

    public CachingCampServiceTests()
    {
        _innerSubstitute = Substitute.For<ICampService, ICampRoleCampAccess, IUserMerge>();
        _innerRoleAccess = (ICampRoleCampAccess)_innerSubstitute;
        var repo = new CampRepository(CampsDbFactory);
        _innerSubstitute.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(ci => LoadSettingsAsync(ci.Arg<CancellationToken>()));
        _innerSubstitute.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => LoadCampsForYearAsync(ci.Arg<int>(), ci.Arg<CancellationToken>()));
        var services = new ServiceCollection();
        services.AddKeyedScoped<ICampService>(
            CachingCampService.InnerServiceKey,
            (_, _) => _innerSubstitute);
        services.AddKeyedScoped<ICampRoleCampAccess>(
            CachingCampService.InnerServiceKey,
            (_, _) => _innerRoleAccess);
        services.AddKeyedScoped<IUserMerge>(
            CachingCampService.InnerServiceKey,
            (_, _) => (IUserMerge)_innerSubstitute);
        _serviceProvider = services.BuildServiceProvider();

        _service = new CachingCampService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Clock,
            NullLogger<CachingCampService>.Instance);

        async Task<CampSettingsInfo> LoadSettingsAsync(CancellationToken ct)
        {
            var settings = await repo.GetSettingsReadOnlyAsync(ct);
            return new CampSettingsInfo(
                settings?.PublicYear ?? Clock.GetCurrentInstant().InUtc().Year,
                settings?.OpenSeasons ?? [],
                settings?.EeStartDate);
        }

        async Task<IReadOnlyList<CampInfo>> LoadCampsForYearAsync(int year, CancellationToken ct)
        {
            var camps = await repo.GetCampsWithLeadsForYearAsync(year, statusFilter: null, ct);
            return camps.Select(ProjectCampInfo).ToList();
        }

    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    // ==========================================================================
    // ICampSeeding.CreateCampForSeedAsync must invalidate, like every other write
    // ==========================================================================

    /// <summary>
    /// The leaf's seeding verb has to go through the decorator's own write path, not straight
    /// to the inner service. Routing it to the inner leaves the all-camps snapshot unchanged,
    /// so every list-based read — the camps index, search, the profile card — is blind to the
    /// new camp until some unrelated later mutation happens to invalidate it. The dev-persona
    /// flow usually approves the season next and hides it; a cancellation between the two
    /// leaves the stale result indefinitely. Caught by Codex on peterdrier/Humans#1288.
    /// </summary>
    [HumansFact]
    public async Task CreateCampForSeedAsync_MakesTheNewCampVisibleToListReads()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var ct = TestContext.Current.CancellationToken;

        // Warm the snapshot first — this is the state the bug needs to show up in.
        var before = await _service.GetCampsForYearAsync(2026, ct);
        before.Should().BeEmpty();

        // The inner service is a substitute here, so stand in for the real create: write the
        // rows the repository would have written, and return the new camp the way it does.
        var seeded = await SeedCampWithSeasonAsync(year: 2026);
        _innerSubstitute.CreateCampAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<List<CampLink>?>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CampSeasonData>(), Arg.Any<List<string>?>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(seeded.camp);

        var newCampId = await ((ICampSeeding)_service).CreateCampForSeedAsync(
            createdByUserId: Guid.NewGuid(),
            name: "Dev Barrio",
            contactEmail: "dev@localhost",
            contactPhone: "+34 600 000 000",
            isSwissCamp: false,
            timesAtNowhere: 0,
            seasonData: SeedSeasonData(),
            year: 2026,
            cancellationToken: ct);

        newCampId.Should().Be(seeded.camp.Id);

        var after = await _service.GetCampsForYearAsync(2026, ct);
        after.Should().ContainSingle(c => c.Id == seeded.camp.Id,
            because: "the seeding verb must invalidate the snapshot the way CreateCampAsync does");
    }

    private static CampSeasonData SeedSeasonData() =>
        new(
            BlurbLong: "long",
            BlurbShort: "short",
            Languages: "English",
            AcceptingMembers: YesNoMaybe.Yes,
            KidsWelcome: YesNoMaybe.No,
            KidsVisiting: KidsVisitingPolicy.DaytimeOnly,
            KidsAreaDescription: null,
            HasPerformanceSpace: PerformanceSpaceStatus.No,
            PerformanceTypes: string.Empty,
            Vibes: [],
            AdultPlayspace: AdultPlayspacePolicy.No,
            MemberCount: 1,
            SpaceRequirement: SpaceSize.Sqm600,
            SoundZone: null,
            ElectricalGrid: null);

    // ==========================================================================
    // P1 — RefreshEntryAsync rebuilds EeGrantedCount from loaded members
    // ==========================================================================

    [HumansFact]
    public async Task InvalidateCampAsync_RefreshesProjectionWithEeGrantedCount()
    {
        // Seed: 2026 is the public year so it's in the warm scope.
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, season) = await SeedCampWithSeasonAsync(year: 2026);
        // Two active members with EE granted, one active without.
        await SeedActiveMemberAsync(season.Id, hasEarlyEntry: true);
        await SeedActiveMemberAsync(season.Id, hasEarlyEntry: true);
        await SeedActiveMemberAsync(season.Id, hasEarlyEntry: false);

        // First read drives the warmup path (GetCampsWithLeadsForYearAsync).
        var initial = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        var warmedSeason = initial
            .Single(c => c.Id == camp.Id)
            .Seasons.Single(s => s.Year == 2026);
        warmedSeason.EeGrantedCount.Should().Be(2,
            because: "warmup loads Seasons.Members and projects the active-EE count");

        // Now flip an existing member's HasEarlyEntry directly in the db and
        // call InvalidateCampAsync — this is the exact path the decorator's
        // write methods (SetEarlyEntryAsync, RemoveCampMemberAsync, …) take:
        // RefreshEntryAsync → _repo.GetByIdAsync → projection.
        var third = CampsDb.CampMembers
            .First(m => m.CampSeasonId == season.Id && !m.HasEarlyEntry);
        third.HasEarlyEntry = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.InvalidateCampAsync(camp.Id, TestContext.Current.CancellationToken);

        var refreshed = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        var refreshedSeason = refreshed
            .Single(c => c.Id == camp.Id)
            .Seasons.Single(s => s.Year == 2026);
        refreshedSeason.EeGrantedCount.Should().Be(3,
            because: "RefreshEntryAsync must load Seasons.Members so the rebuilt CampInfo reflects the new EE count — without the Include, the projection sees zero members and reports 0");
    }

    // ==========================================================================
    // P2 — cold-year fallback to inner service
    // ==========================================================================

    [HumansFact]
    public async Task GetCampsForYearAsync_ColdYear_FallsBackToInnerService()
    {
        // Warm scope: 2026 only (public + open + currentYear all equal 2026).
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        await SeedCampWithSeasonAsync(year: 2026);

        // Drive warmup once so the warm-year set is populated.
        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);

        // Request a cold year — must NOT return empty even though the snapshot
        // has no rows for 2023. Inner-substitute returns a known sentinel.
        var coldYearResult = new List<CampInfo>
        {
            new(
                Id: Guid.NewGuid(),
                Slug: "historic-camp",
                ContactEmail: "x@example.com",
                ContactPhone: "+34000000000",
                IsSwissCamp: false,
                TimesAtNowhere: 1,
                Seasons: [])
        };
        _innerSubstitute
            .GetCampsForYearAsync(2023, Arg.Any<CancellationToken>())
            .Returns(coldYearResult);

        var actual = await _service.GetCampsForYearAsync(2023, TestContext.Current.CancellationToken);

        actual.Should().BeEquivalentTo(coldYearResult,
            because: "years outside the warm scope must fall back to the inner service rather than return an empty snapshot slice");
        await _innerSubstitute.Received(1).GetCampsForYearAsync(2023, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_WarmYear_DoesNotHitInner()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        await SeedCampWithSeasonAsync(year: 2026);

        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        _innerSubstitute.ClearReceivedCalls();
        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);

        await _innerSubstitute
            .DidNotReceive()
            .GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_WarmCamp_ExposesCampInfoLeadRoleFacts()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var leadUserId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var cachedCamps = new List<CampInfo>
        {
            new(
                Id: campId,
                Slug: "role-camp",
                ContactEmail: "role@example.com",
                ContactPhone: "+34000000000",
                IsSwissCamp: false,
                TimesAtNowhere: 1,
                Seasons:
                [
                    new CampSeasonInfo(
                        seasonId,
                        campId,
                        "role-camp",
                        2026,
                        null,
                        "Role Camp",
                        "Role camp",
                        "en",
                        [],
                        CampSeasonStatus.Active,
                        YesNoMaybe.Yes,
                        YesNoMaybe.No,
                        AdultPlayspacePolicy.No,
                        MemberCount: 1,
                        SoundZone: null,
                        SpaceRequirement: null,
                        ElectricalGrid: null,
                        EeSlotCount: 0,
                        EeGrantedCount: 0,
                        JoinedMemberCount: 1)
                    {
                        LeadUserIds = [leadUserId]
                    }
                ])
        };
        _innerSubstitute.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(cachedCamps);

        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        _innerSubstitute.ClearReceivedCalls();

        var camp = (await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken)).Single(c => c.Id == campId);
        camp.IsLead(leadUserId).Should().BeTrue();

        await _innerSubstitute
            .DidNotReceive()
            .GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampSeasonByIdAsync_WarmSeason_UsesCachedCampInfo()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var leadUserId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var cachedSeason = new CampSeasonInfo(
            seasonId,
            campId,
            "season-camp",
            2026,
            null,
            "Season Camp",
            "Season camp",
            "en",
            [],
            CampSeasonStatus.Active,
            YesNoMaybe.Yes,
            YesNoMaybe.No,
            AdultPlayspacePolicy.No,
            MemberCount: 1,
            SoundZone: null,
            SpaceRequirement: null,
            ElectricalGrid: null,
            EeSlotCount: 0,
            EeGrantedCount: 0,
            JoinedMemberCount: 1)
        {
            LeadUserIds = [leadUserId]
        };
        var cachedCamps = new List<CampInfo>
        {
            new(
                Id: campId,
                Slug: "season-camp",
                ContactEmail: "season@example.com",
                ContactPhone: "+34000000000",
                IsSwissCamp: false,
                TimesAtNowhere: 1,
                Seasons: [cachedSeason])
        };
        _innerSubstitute.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(cachedCamps);

        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        _innerSubstitute.ClearReceivedCalls();

        var actual = await _service.GetCampSeasonByIdAsync(seasonId, TestContext.Current.CancellationToken);

        actual.Should().BeSameAs(cachedSeason);
        await _innerSubstitute
            .DidNotReceive()
            .GetCampSeasonByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampSeasonsForComplianceAsync_WarmYear_ProjectsFromCachedCampInfo()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, season) = await SeedCampWithSeasonAsync(year: 2026);

        _ = await _service.GetCampsForYearAsync(2026, TestContext.Current.CancellationToken);
        _innerSubstitute.ClearReceivedCalls();
        _innerRoleAccess.ClearReceivedCalls();

        var result = await _service.GetCampSeasonsForComplianceAsync(2026, TestContext.Current.CancellationToken);

        result.Should().ContainSingle(item =>
            item.CampId == camp.Id &&
            item.CampName == season.Name &&
            item.CampSlug == camp.Slug &&
            item.CampSeasonId == season.Id);
        await _innerSubstitute
            .DidNotReceive()
            .GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _innerRoleAccess
            .DidNotReceive()
            .GetCampSeasonsForComplianceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetEarlyEntriesAsync_WarmYear_ProjectsFromCachedCampInfoMembers()
    {
        var eeStartDate = new LocalDate(2026, 7, 7);
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026], eeStartDate);
        var (_, season) = await SeedCampWithSeasonAsync(year: 2026);
        var granted = await SeedActiveMemberAsync(season.Id, hasEarlyEntry: true);
        await SeedActiveMemberAsync(season.Id, hasEarlyEntry: false);
        await SeedMemberAsync(season.Id, CampMemberStatus.Pending, hasEarlyEntry: true);

        var grants = await _service.GetEarlyEntriesAsync(TestContext.Current.CancellationToken);

        grants.Should().ContainSingle()
            .Which.Should().Be(new EarlyEntryGrant(granted.UserId, eeStartDate, "Camp: Test Camp"));
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task SeedSettingsAsync(
        int publicYear,
        List<int> openSeasons,
        LocalDate? eeStartDate = null)
    {
        if (!await CampsDb.CampSettings.AnyAsync(TestContext.Current.CancellationToken))
        {
            CampsDb.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = publicYear,
                OpenSeasons = openSeasons,
                EeStartDate = eeStartDate
            });
            await SaveAllAsync(TestContext.Current.CancellationToken);
        }
    }

    [HumansFact]
    public async Task SearchAsync_ServesFromCache_MatchesPublicYearSeasonName()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, _) = await SeedCampWithSeasonAsync(year: 2026); // season "Test Camp", Active

        var results = await _service.SearchAsync("test camp", int.MaxValue, TestContext.Current.CancellationToken);

        var hit = results.Should().ContainSingle().Subject;
        hit.CampId.Should().Be(camp.Id);
        hit.Name.Should().Be("Test Camp");
        // Scoring belongs to the section, not the global-search orchestrator
        // (nobodies-collective/Humans#1062).
        hit.Score.Should().Be(StringSearchExtensions.ExactNameScore);

        // Search must never reach the inner service's SearchAsync (the DB ILike path is gone).
        await _innerSubstitute.DidNotReceive().SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Timeout raised from the 5s HumansTheory default: this is the third of three
    // sequential inline cases, each paying full CampsTestHarness + EF InMemory
    // setup, and flaked under CI load with "Test execution timed out after 5000
    // milliseconds" on the Withdrawn case (never Pending/Rejected) — 4 times in
    // one week, twice confirmed passing on retry against the identical commit.
    // 10s matches the codebase's standard bump for CI-load-sensitive tests.
    [HumansTheory(Timeout = 10000)]
    [InlineData(CampSeasonStatus.Pending)]
    [InlineData(CampSeasonStatus.Rejected)]
    [InlineData(CampSeasonStatus.Withdrawn)]
    public async Task SearchAsync_TextQuery_ExcludesNonPublicSeasonStatuses(CampSeasonStatus status)
    {
        // The ruling on nobodies-collective/Humans#985 narrowed the privacy guarantee to
        // by-GUID lookups only — text queries are unchanged and still see Active/Full alone.
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        await SeedCampWithSeasonAsync(year: 2026, status);

        var results = await _service.SearchAsync(
            "test camp", int.MaxValue, TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ResolvesACampWithANonPublicSeason()
    {
        // Routing convenience, not authorization: the caller already holds the camp id and
        // /Camps/{slug} is where the decision belongs.
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, _) = await SeedCampWithSeasonAsync(year: 2026, CampSeasonStatus.Pending);

        var results = await _service.SearchAsync(
            camp.Id.ToString(), int.MaxValue, TestContext.Current.CancellationToken);

        var hit = results.Should().ContainSingle().Subject;
        hit.CampId.Should().Be(camp.Id);
        hit.Score.Should().Be(StringSearchExtensions.ExactNameScore,
            because: "an id paste is as exact as a match gets");
    }

    // ==========================================================================
    // T4 — every write verb invalidates its slice of the cache
    // ==========================================================================

    public static TheoryData<string, Func<CachingCampServiceTests, Guid, Guid, CancellationToken, Task>> SnapshotWriteVerbs => new()
    {
        { nameof(ICampService.CreateCampAsync), (t, c, se, ct) => t._service.CreateCampAsync(Guid.NewGuid(), "New Camp", "n@x.com", "+34600000001", null, null, false, 1, SeedSeasonData(), null, 2026, ct) },
        { nameof(ICampService.OptInToSeasonAsync), (t, c, se, ct) => t._service.OptInToSeasonAsync(c, 2026, ct) },
        { nameof(ICampService.UpdateSeasonAsync), (t, c, se, ct) => t._service.UpdateSeasonAsync(c, se, SeedSeasonData(), ct) },
        { nameof(ICampService.ApproveSeasonAsync), (t, c, se, ct) => t._service.ApproveSeasonAsync(se, Guid.NewGuid(), null, ct) },
        { nameof(ICampService.RejectSeasonAsync), (t, c, se, ct) => t._service.RejectSeasonAsync(se, Guid.NewGuid(), "notes", ct) },
        { nameof(ICampService.WithdrawSeasonAsync), (t, c, se, ct) => t._service.WithdrawSeasonAsync(c, se, ct) },
        { nameof(ICampService.ReactivateSeasonAsync), (t, c, se, ct) => t._service.ReactivateSeasonAsync(se, ct) },
        { nameof(ICampService.SetSeasonStatusAsync), (t, c, se, ct) => t._service.SetSeasonStatusAsync(c, se, CampSeasonStatus.Full, ct) },
        { nameof(ICampService.ChangeSeasonNameAsync), (t, c, se, ct) => t._service.ChangeSeasonNameAsync(c, se, "New Name", ct) },
        { nameof(ICampService.UpdateCampAsync), (t, c, se, ct) => t._service.UpdateCampAsync(new CampUpdateInput(c, "e@x.com", "+34600000000", null, null, false, 1, false, se, "Name", SeedSeasonData()), ct) },
        { nameof(ICampService.AddHistoricalNameAsync), (t, c, se, ct) => t._service.AddHistoricalNameAsync(c, "Old Name", ct) },
        { nameof(ICampService.RemoveHistoricalNameAsync), (t, c, se, ct) => t._service.RemoveHistoricalNameAsync(c, Guid.NewGuid(), ct) },
        { nameof(ICampService.UploadImageAsync), (t, c, se, ct) => t._service.UploadImageAsync(c, Stream.Null, "a.jpg", "image/jpeg", 1, ct) },
        { nameof(ICampService.DeleteImageAsync), (t, c, se, ct) => t._service.DeleteImageAsync(c, Guid.NewGuid(), ct) },
        { nameof(ICampService.ReorderImagesAsync), (t, c, se, ct) => t._service.ReorderImagesAsync(c, [], ct) },
        { nameof(ICampService.SetNameLockDateAsync), (t, c, se, ct) => t._service.SetNameLockDateAsync(2026, new LocalDate(2026, 5, 1), ct) },
        { nameof(ICampService.RequestCampMembershipAsync), (t, c, se, ct) => t._service.RequestCampMembershipAsync(c, Guid.NewGuid(), ct) },
        { nameof(ICampService.ApproveCampMemberAsync), (t, c, se, ct) => t._service.ApproveCampMemberAsync(c, Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.RejectCampMemberAsync), (t, c, se, ct) => t._service.RejectCampMemberAsync(c, Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.RemoveCampMemberAsync), (t, c, se, ct) => t._service.RemoveCampMemberAsync(c, Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.AddCampMemberToActiveSeasonAsync), (t, c, se, ct) => t._service.AddCampMemberToActiveSeasonAsync(c, Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.AddMemberAndAssignRoleInActiveSeasonAsync), (t, c, se, ct) => t._service.AddMemberAndAssignRoleInActiveSeasonAsync(c, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.WithdrawCampMembershipRequestAsync), (t, c, se, ct) => t._service.WithdrawCampMembershipRequestAsync(Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.LeaveCampAsync), (t, c, se, ct) => t._service.LeaveCampAsync(Guid.NewGuid(), Guid.NewGuid(), ct) },
        { nameof(ICampService.SetEarlyEntryAsync), (t, c, se, ct) => t._service.SetEarlyEntryAsync(c, Guid.NewGuid(), true, Guid.NewGuid(), ct) },
        { nameof(ICampService.SetCampSeasonEeSlotCountAsync), (t, c, se, ct) => t._service.SetCampSeasonEeSlotCountAsync(se, 3, Guid.NewGuid(), ct) },
        { nameof(IUserMerge.ReassignAsync), (t, c, se, ct) => ((IUserMerge)t._service).ReassignAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Instant.FromUtc(2026, 3, 13, 12, 0), ct) },
    };

    /// <summary>
    /// Pins "every mutating path invalidates" over the whole write surface: after any
    /// write verb, a warm-year read must re-warm from the inner service instead of
    /// serving the pre-write snapshot. Timeout matches the CI-load bump above.
    /// </summary>
    [HumansTheory(Timeout = 10000)]
    [MemberData(nameof(SnapshotWriteVerbs))]
    public async Task WriteVerb_DropsTheCampSnapshot_SoTheNextReadRewarms(
        string verbName, Func<CachingCampServiceTests, Guid, Guid, CancellationToken, Task> invoke)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, season) = await SeedCampWithSeasonAsync(year: 2026);
        StubSucceedingWriteResults(camp);

        _ = await _service.GetCampsForYearAsync(2026, ct);
        _innerSubstitute.ClearReceivedCalls();

        await invoke(this, camp.Id, season.Id, ct);

        _ = await _service.GetCampsForYearAsync(2026, ct);
        _innerSubstitute.ReceivedCalls()
            .Count(call => string.Equals(call.GetMethodInfo().Name, nameof(ICampService.GetCampsForYearAsync), StringComparison.Ordinal))
            .Should().Be(1, because: $"{verbName} must invalidate the cached CampInfo snapshot so the next warm-year read re-warms from the inner service");
    }

    public static TheoryData<string, Func<CachingCampServiceTests, CancellationToken, Task>> SettingsWriteVerbs => new()
    {
        { nameof(ICampService.SetPublicYearAsync), (t, ct) => t._service.SetPublicYearAsync(2027, ct) },
        { nameof(ICampService.OpenSeasonAsync), (t, ct) => t._service.OpenSeasonAsync(2027, ct) },
        { nameof(ICampService.CloseSeasonAsync), (t, ct) => t._service.CloseSeasonAsync(2026, ct) },
        { nameof(ICampService.SetNameLockDateAsync), (t, ct) => t._service.SetNameLockDateAsync(2026, new LocalDate(2026, 5, 1), ct) },
        { nameof(ICampService.SetEeStartDateAsync), (t, ct) => t._service.SetEeStartDateAsync(new LocalDate(2026, 7, 7), Guid.NewGuid(), ct) },
    };

    [HumansTheory(Timeout = 10000)]
    [MemberData(nameof(SettingsWriteVerbs))]
    public async Task SettingsWriteVerb_ClearsTheSettingsSlot_SoTheNextReadRefetches(
        string verbName, Func<CachingCampServiceTests, CancellationToken, Task> invoke)
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);

        _ = await _service.GetSettingsAsync(ct);
        _innerSubstitute.ClearReceivedCalls();

        await invoke(this, ct);

        _ = await _service.GetSettingsAsync(ct);
        _innerSubstitute.ReceivedCalls()
            .Count(call => string.Equals(call.GetMethodInfo().Name, nameof(ICampService.GetSettingsAsync), StringComparison.Ordinal))
            .Should().Be(1, because: $"{verbName} must clear the cached settings slot so the next read refetches from the inner service");
    }

    [HumansFact]
    public async Task DeleteCampAsync_TombstonesTheCamp_WithoutDroppingTheWholeSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        var (camp, _) = await SeedCampWithSeasonAsync(year: 2026);

        _ = await _service.GetCampsForYearAsync(2026, ct);
        _innerSubstitute.ClearReceivedCalls();

        await _service.DeleteCampAsync(camp.Id, ct);

        var after = await _service.GetCampsForYearAsync(2026, ct);
        after.Should().NotContain(c => c.Id == camp.Id,
            because: "the deleted camp must leave the snapshot immediately");
        await _innerSubstitute.DidNotReceive().GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Success-path stubs for the write verbs whose decorator only invalidates when
    /// the inner result reports success.
    /// </summary>
    private void StubSucceedingWriteResults(Camp camp)
    {
        _innerSubstitute.UpdateCampAsync(Arg.Any<CampUpdateInput>(), Arg.Any<CancellationToken>())
            .Returns(CampUpdateResult.Success());
        _innerSubstitute.UploadImageAsync(
                Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new CampImageUploadResult(true, null, null));
        _innerSubstitute.LeaveCampAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(CampMembershipMutationResult.Success());
        _innerSubstitute.CreateCampAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<List<CampLink>?>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CampSeasonData>(), Arg.Any<List<string>?>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(camp);
    }

    private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(
        int year, CampSeasonStatus status = CampSeasonStatus.Active)
    {
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = $"camp-{Guid.NewGuid():N}".Substring(0, 12),
            ContactEmail = "test@camp.com",
            ContactPhone = "+34600000000",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = year,
            Status = status,
            Name = "Test Camp",
            EeSlotCount = 5,
            BlurbLong = "A fun camp",
            BlurbShort = "Fun",
            Languages = "en",
            AcceptingMembers = YesNoMaybe.Yes,
            KidsWelcome = YesNoMaybe.Maybe,
            KidsVisiting = KidsVisitingPolicy.DaytimeOnly,
            HasPerformanceSpace = PerformanceSpaceStatus.No,
            Vibes = [CampVibe.LiveMusic],
            AdultPlayspace = AdultPlayspacePolicy.No,
            MemberCount = 10,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        CampsDb.Camps.Add(camp);
        CampsDb.CampSeasons.Add(season);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        return (camp, season);
    }

    private Task<CampMember> SeedActiveMemberAsync(Guid campSeasonId, bool hasEarlyEntry) =>
        SeedMemberAsync(campSeasonId, CampMemberStatus.Active, hasEarlyEntry);

    private async Task<CampMember> SeedMemberAsync(
        Guid campSeasonId,
        CampMemberStatus status,
        bool hasEarlyEntry)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = Guid.NewGuid(),
            Status = status,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = status == CampMemberStatus.Active ? Clock.GetCurrentInstant() : null,
            HasEarlyEntry = hasEarlyEntry,
        };
        CampsDb.CampMembers.Add(member);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        return member;
    }

    private static CampInfo ProjectCampInfo(Camp camp) => new(
        camp.Id,
        camp.Slug,
        camp.ContactEmail,
        camp.ContactPhone,
        camp.IsSwissCamp,
        camp.TimesAtNowhere,
        camp.Seasons.Select(ProjectSeasonInfo).ToList());

    private static CampSeasonInfo ProjectSeasonInfo(CampSeason season) => new(
            season.Id,
            season.CampId,
            season.Camp?.Slug ?? string.Empty,
            season.Year,
            season.NameLockDate,
            season.Name,
            season.BlurbShort,
            season.Languages,
            season.Vibes.ToList(),
            season.Status,
            season.AcceptingMembers,
            season.KidsWelcome,
            season.AdultPlayspace,
            season.MemberCount,
            season.SoundZone,
            season.SpaceRequirement,
            season.ElectricalGrid,
            season.EeSlotCount,
            season.Members.Count(m => m.Status == CampMemberStatus.Active && m.HasEarlyEntry),
            season.Members.Count(m => m.Status == CampMemberStatus.Active))
    {
        Members = season.Members
                .Where(m => m.Status != CampMemberStatus.Removed)
                .OrderBy(m => m.RequestedAt)
                .Select(m => new CampSeasonMemberInfo(
                    m.Id,
                    m.UserId,
                    m.Status,
                    m.RequestedAt,
                    m.ConfirmedAt,
                    m.HasEarlyEntry))
                .ToList()
    };
}

using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Infrastructure.Services.Camps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

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
public sealed class CachingCampServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly ServiceProvider _serviceProvider;
    private readonly ICampService _innerSubstitute;
    private readonly CachingCampService _service;

    public CachingCampServiceTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(_options);

        _innerSubstitute = Substitute.For<ICampService>();
        var services = new ServiceCollection();
        services.AddKeyedScoped<ICampService>(
            CachingCampService.InnerServiceKey,
            (_, _) => _innerSubstitute);
        _serviceProvider = services.BuildServiceProvider();

        var repo = new CampRepository(new TestDbContextFactory(_options));
        _service = new CachingCampService(
            repo,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            NullLogger<CachingCampService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

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
        var initial = await _service.GetCampsForYearAsync(2026);
        var warmedSeason = initial
            .Single(c => c.Id == camp.Id)
            .Seasons.Single(s => s.Year == 2026);
        warmedSeason.EeGrantedCount.Should().Be(2,
            because: "warmup loads Seasons.Members and projects the active-EE count");

        // Now flip an existing member's HasEarlyEntry directly in the db and
        // call InvalidateCampAsync — this is the exact path the decorator's
        // write methods (SetEarlyEntryAsync, RemoveCampMemberAsync, …) take:
        // RefreshEntryAsync → _repo.GetByIdAsync → projection.
        var third = _dbContext.CampMembers
            .First(m => m.CampSeasonId == season.Id && !m.HasEarlyEntry);
        third.HasEarlyEntry = true;
        await _dbContext.SaveChangesAsync();

        await _service.InvalidateCampAsync(camp.Id);

        var refreshed = await _service.GetCampsForYearAsync(2026);
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
        _ = await _service.GetCampsForYearAsync(2026);

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
                Seasons: [],
                Leads: [])
        };
        _innerSubstitute
            .GetCampsForYearAsync(2023, Arg.Any<CancellationToken>())
            .Returns(coldYearResult);

        var actual = await _service.GetCampsForYearAsync(2023);

        actual.Should().BeEquivalentTo(coldYearResult,
            because: "years outside the warm scope must fall back to the inner service rather than return an empty snapshot slice");
        await _innerSubstitute.Received(1).GetCampsForYearAsync(2023, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_WarmYear_DoesNotHitInner()
    {
        await SeedSettingsAsync(publicYear: 2026, openSeasons: [2026]);
        await SeedCampWithSeasonAsync(year: 2026);

        _ = await _service.GetCampsForYearAsync(2026);
        _ = await _service.GetCampsForYearAsync(2026);

        await _innerSubstitute
            .DidNotReceive()
            .GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task SeedSettingsAsync(int publicYear, List<int> openSeasons)
    {
        if (!await _dbContext.CampSettings.AnyAsync())
        {
            _dbContext.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = publicYear,
                OpenSeasons = openSeasons
            });
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(int year)
    {
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = $"camp-{Guid.NewGuid():N}".Substring(0, 12),
            ContactEmail = "test@camp.com",
            ContactPhone = "+34600000000",
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = year,
            Status = CampSeasonStatus.Active,
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
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Camps.Add(camp);
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync();
        return (camp, season);
    }

    private async Task SeedActiveMemberAsync(Guid campSeasonId, bool hasEarlyEntry)
    {
        _dbContext.CampMembers.Add(new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
            HasEarlyEntry = hasEarlyEntry,
        });
        await _dbContext.SaveChangesAsync();
    }
}

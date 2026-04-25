using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class CampRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampRepository _repo;

    public CampRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new CampRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // SlugExistsAsync / CreateCampAsync
    // ==========================================================================

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsTrue_WhenCampPresent()
    {
        var camp = await SeedCampAsync("test-camp");

        var exists = await _repo.SlugExistsAsync(camp.Slug);

        exists.Should().BeTrue();
    }

    [HumansFact]
    public async Task SlugExistsAsync_ReturnsFalse_WhenNoMatch()
    {
        var exists = await _repo.SlugExistsAsync("no-such-camp");

        exists.Should().BeFalse();
    }

    [HumansFact]
    public async Task CreateCampAsync_PersistsAllAggregateRows()
    {
        var camp = BuildCamp("new-camp");
        var season = BuildSeason(camp.Id, CampSeasonStatus.Pending, 2026);
        var lead = BuildLead(camp.Id, Guid.NewGuid());
        var history = new List<CampHistoricalName>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CampId = camp.Id,
                Name = "Old Name",
                Source = CampNameSource.Manual,
                CreatedAt = _clock.GetCurrentInstant()
            }
        };

        await _repo.CreateCampAsync(camp, season, lead, history);

        var persistedCamp = await _dbContext.Camps.AsNoTracking().FirstAsync(c => c.Id == camp.Id);
        persistedCamp.Slug.Should().Be("new-camp");
        (await _dbContext.CampSeasons.AsNoTracking().CountAsync(s => s.CampId == camp.Id)).Should().Be(1);
        (await _dbContext.CampLeads.AsNoTracking().CountAsync(l => l.CampId == camp.Id)).Should().Be(1);
        (await _dbContext.CampHistoricalNames.AsNoTracking().CountAsync(h => h.CampId == camp.Id)).Should().Be(1);
    }

    // ==========================================================================
    // IsUserActiveLeadAsync / CountActiveLeadsAsync / GetActiveLeadUserIdsAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserActiveLeadAsync_ReturnsTrue_WhenActive()
    {
        var userId = Guid.NewGuid();
        var camp = await SeedCampAsync("active-lead-camp");
        await SeedLeadAsync(camp.Id, userId, leftAt: null);

        var result = await _repo.IsUserActiveLeadAsync(userId, camp.Id);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserActiveLeadAsync_ReturnsFalse_WhenLeft()
    {
        var userId = Guid.NewGuid();
        var camp = await SeedCampAsync("left-lead-camp");
        await SeedLeadAsync(camp.Id, userId, leftAt: _clock.GetCurrentInstant());

        var result = await _repo.IsUserActiveLeadAsync(userId, camp.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetActiveLeadUserIdsAsync_ReturnsDistinctActiveLeadUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var camp1 = await SeedCampAsync("camp-1");
        var camp2 = await SeedCampAsync("camp-2");

        await SeedLeadAsync(camp1.Id, userA, leftAt: null);
        await SeedLeadAsync(camp2.Id, userA, leftAt: null); // same user, different camp
        await SeedLeadAsync(camp2.Id, userB, leftAt: null);
        await SeedLeadAsync(camp1.Id, Guid.NewGuid(), leftAt: _clock.GetCurrentInstant()); // inactive

        var result = await _repo.GetActiveLeadUserIdsAsync();

        result.Should().BeEquivalentTo([userA, userB]);
    }

    // ==========================================================================
    // SeasonExistsAsync / HasApprovedSeasonAsync
    // ==========================================================================

    [HumansFact]
    public async Task SeasonExistsAsync_ReturnsTrue_WhenPresent()
    {
        var camp = await SeedCampAsync("season-camp");
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Pending);

        (await _repo.SeasonExistsAsync(camp.Id, 2026)).Should().BeTrue();
    }

    [HumansFact]
    public async Task SeasonExistsAsync_ReturnsFalse_WhenDifferentYear()
    {
        var camp = await SeedCampAsync("other-year");
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Active);

        (await _repo.SeasonExistsAsync(camp.Id, 2027)).Should().BeFalse();
    }

    [HumansFact]
    public async Task HasApprovedSeasonAsync_ReturnsTrue_WhenAnyActiveFullOrWithdrawn()
    {
        var camp = await SeedCampAsync("approved-camp");
        await SeedSeasonAsync(camp.Id, 2025, CampSeasonStatus.Withdrawn);
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Pending);

        (await _repo.HasApprovedSeasonAsync(camp.Id)).Should().BeTrue();
    }

    [HumansFact]
    public async Task HasApprovedSeasonAsync_ReturnsFalse_WhenAllPendingOrRejected()
    {
        var camp = await SeedCampAsync("never-approved");
        await SeedSeasonAsync(camp.Id, 2025, CampSeasonStatus.Rejected);
        await SeedSeasonAsync(camp.Id, 2026, CampSeasonStatus.Pending);

        (await _repo.HasApprovedSeasonAsync(camp.Id)).Should().BeFalse();
    }

    // ==========================================================================
    // GetPublicCampsForYearAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetPublicCampsForYearAsync_ReturnsActiveAndFull_ExcludesPendingAndRejected()
    {
        var activeCamp = await SeedCampAsync("active");
        await SeedSeasonAsync(activeCamp.Id, 2026, CampSeasonStatus.Active);

        var fullCamp = await SeedCampAsync("full");
        await SeedSeasonAsync(fullCamp.Id, 2026, CampSeasonStatus.Full);

        var pendingCamp = await SeedCampAsync("pending");
        await SeedSeasonAsync(pendingCamp.Id, 2026, CampSeasonStatus.Pending);

        var result = await _repo.GetPublicCampsForYearAsync(2026);

        result.Select(c => c.Slug).Should().BeEquivalentTo(["active", "full"]);
    }

    // ==========================================================================
    // Image tests
    // ==========================================================================

    [HumansFact]
    public async Task CountImagesAsync_ReturnsCount()
    {
        var camp = await SeedCampAsync("image-camp");
        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            FileName = "a",
            StoragePath = "a",
            ContentType = "image/jpeg",
            SortOrder = 0,
            UploadedAt = _clock.GetCurrentInstant()
        });
        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            FileName = "b",
            StoragePath = "b",
            ContentType = "image/jpeg",
            SortOrder = 1,
            UploadedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        (await _repo.CountImagesAsync(camp.Id)).Should().Be(2);
    }

    // ==========================================================================
    // Settings tests
    // ==========================================================================

    [HumansFact]
    public async Task GetSettingsReadOnlyAsync_ReturnsRow()
    {
        _dbContext.CampSettings.Add(new CampSettings
        {
            Id = Guid.NewGuid(),
            PublicYear = 2026,
            OpenSeasons = new List<int> { 2026 }
        });
        await _dbContext.SaveChangesAsync();

        var settings = await _repo.GetSettingsReadOnlyAsync();

        settings.Should().NotBeNull();
        settings!.PublicYear.Should().Be(2026);
    }

    [HumansFact]
    public async Task OpenSeasonAsync_AddsWhenMissing_ReturnsTrue()
    {
        await SeedSettingsAsync();

        var changed = await _repo.OpenSeasonAsync(2027);

        changed.Should().BeTrue();
        var settings = await _dbContext.CampSettings.AsNoTracking().FirstAsync();
        settings.OpenSeasons.Should().Contain(2027);
    }

    [HumansFact]
    public async Task OpenSeasonAsync_NoOp_WhenAlreadyOpen()
    {
        await SeedSettingsAsync();
        await _repo.OpenSeasonAsync(2027);

        var changed = await _repo.OpenSeasonAsync(2027);

        changed.Should().BeFalse();
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<Camp> SeedCampAsync(string slug)
    {
        var camp = BuildCamp(slug);
        _dbContext.Camps.Add(camp);
        await _dbContext.SaveChangesAsync();
        return camp;
    }

    private async Task SeedSeasonAsync(Guid campId, int year, CampSeasonStatus status)
    {
        _dbContext.CampSeasons.Add(BuildSeason(campId, status, year));
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedLeadAsync(Guid campId, Guid userId, Instant? leftAt)
    {
        _dbContext.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = campId,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = _clock.GetCurrentInstant(),
            LeftAt = leftAt
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedSettingsAsync()
    {
        _dbContext.CampSettings.Add(new CampSettings
        {
            Id = Guid.NewGuid(),
            PublicYear = 2026,
            OpenSeasons = new List<int> { 2026 }
        });
        await _dbContext.SaveChangesAsync();
    }

    private Camp BuildCamp(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        ContactEmail = $"{slug}@example.com",
        ContactPhone = "+34600000000",
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };

    private CampSeason BuildSeason(Guid campId, CampSeasonStatus status, int year) => new()
    {
        Id = Guid.NewGuid(),
        CampId = campId,
        Year = year,
        Name = $"Season-{year}",
        Status = status,
        BlurbLong = "long",
        BlurbShort = "short",
        Languages = "EN",
        AcceptingMembers = YesNoMaybe.Yes,
        KidsWelcome = YesNoMaybe.No,
        KidsVisiting = KidsVisitingPolicy.DaytimeOnly,
        HasPerformanceSpace = PerformanceSpaceStatus.No,
        Vibes = new List<CampVibe>(),
        AdultPlayspace = AdultPlayspacePolicy.No,
        MemberCount = 10,
        ContainerCount = 0,
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };

    private CampLead BuildLead(Guid campId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        CampId = campId,
        UserId = userId,
        Role = CampLeadRole.CoLead,
        JoinedAt = _clock.GetCurrentInstant()
    };
}

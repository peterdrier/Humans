using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

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
        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
        };
        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            CampRoleDefinitionId = leadDef.Id,
            CampMemberId = member.Id,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = member.UserId,
        };
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

        await _repo.CreateCampAsync(camp, season, member, assignment, history);

        var persistedCamp = await _dbContext.Camps.AsNoTracking().FirstAsync(c => c.Id == camp.Id);
        persistedCamp.Slug.Should().Be("new-camp");
        (await _dbContext.CampSeasons.AsNoTracking().CountAsync(s => s.CampId == camp.Id)).Should().Be(1);
        (await _dbContext.CampMembers.AsNoTracking().CountAsync(m => m.CampSeasonId == season.Id)).Should().Be(1);
        (await _dbContext.CampRoleAssignments.AsNoTracking().CountAsync(a => a.CampSeasonId == season.Id)).Should().Be(1);
        (await _dbContext.CampHistoricalNames.AsNoTracking().CountAsync(h => h.CampId == camp.Id)).Should().Be(1);
    }

    // ==========================================================================
    // GetActiveLeadUserIdsAsync (role-backed)
    // ==========================================================================

    [HumansFact]
    public async Task GetActiveLeadUserIdsAsync_ReturnsDistinctActiveLeadUsers()
    {
        // Post-Camp-Lead-retirement (issue nobodies-collective/Humans#753):
        // GetActiveLeadUserIdsAsync now reads from CampRoleAssignment where
        // SpecialRole == Lead, not the legacy camp_leads table.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var camp1 = await SeedCampAsync("camp-1");
        var camp2 = await SeedCampAsync("camp-2");

        var leadDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Lead);
        var workshopDef = await SeedSpecialRoleDefinitionAsync(CampSpecialRole.Workshop);
        var regularDef = await SeedRegularRoleDefinitionAsync();
        await SeedLeadAssignmentAsync(camp1.Id, leadDef.Id, userA);
        await SeedLeadAssignmentAsync(camp2.Id, leadDef.Id, userA); // same user, different camp
        await SeedLeadAssignmentAsync(camp2.Id, leadDef.Id, userB);
        // Negative — Workshop and regular role holders are NOT returned.
        await SeedLeadAssignmentAsync(camp1.Id, workshopDef.Id, Guid.NewGuid());
        await SeedLeadAssignmentAsync(camp1.Id, regularDef.Id, Guid.NewGuid());

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
    // Camp.HasPublicSeasonForYear predicate (caller-side filter on GetAllCampsForYearAsync)
    // ==========================================================================

    [HumansFact]
    public async Task GetAllCampsForYearAsync_WithHasPublicSeasonFilter_ReturnsActiveAndFull()
    {
        var activeCamp = await SeedCampAsync("active");
        await SeedSeasonAsync(activeCamp.Id, 2026, CampSeasonStatus.Active);

        var fullCamp = await SeedCampAsync("full");
        await SeedSeasonAsync(fullCamp.Id, 2026, CampSeasonStatus.Full);

        var pendingCamp = await SeedCampAsync("pending");
        await SeedSeasonAsync(pendingCamp.Id, 2026, CampSeasonStatus.Pending);

        var result = await _repo.GetAllCampsForYearAsync(2026);

        result.Where(c => c.HasPublicSeasonForYear(2026)).Select(c => c.Slug)
            .Should().BeEquivalentTo("active", "full");
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
            OpenSeasons = [2026]
        });
        await _dbContext.SaveChangesAsync();

        var settings = await _repo.GetSettingsReadOnlyAsync();

        settings.Should().NotBeNull();
        settings.PublicYear.Should().Be(2026);
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

    private async Task SeedSettingsAsync()
    {
        _dbContext.CampSettings.Add(new CampSettings
        {
            Id = Guid.NewGuid(),
            PublicYear = 2026,
            OpenSeasons = [2026]
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
        Vibes = [],
        AdultPlayspace = AdultPlayspacePolicy.No,
        MemberCount = 10,
        CreatedAt = _clock.GetCurrentInstant(),
        UpdatedAt = _clock.GetCurrentInstant()
    };

    private async Task<CampRoleDefinition> SeedSpecialRoleDefinitionAsync(CampSpecialRole specialRole)
    {
        var slug = specialRole.ToString().ToLowerInvariant() + "-test";
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"{specialRole} Test Definition",
            Slug = slug,
            SlotCount = 5,
            MinimumRequired = 0,
            SortOrder = 0,
            SpecialRole = specialRole,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync();
        return def;
    }

    private async Task<CampRoleDefinition> SeedRegularRoleDefinitionAsync()
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"Regular Role {Guid.NewGuid():N}".Substring(0, 30),
            Slug = $"regular-{Guid.NewGuid():N}".Substring(0, 20),
            SlotCount = 1,
            MinimumRequired = 0,
            SortOrder = 100,
            SpecialRole = CampSpecialRole.None,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync();
        return def;
    }

    private async Task<CampRoleAssignment> SeedLeadAssignmentAsync(Guid campId, Guid roleDefinitionId, Guid userId)
    {
        // Helper that lands a CampRoleAssignment for the given (camp, role, user)
        // via the camp's current season + a CampMember(Active). Used by tests
        // that exercise the role-assignment-based lead authority.
        var season = _dbContext.CampSeasons.FirstOrDefault(s => s.CampId == campId);
        if (season is null)
        {
            season = BuildSeason(campId, CampSeasonStatus.Active, year: 2026);
            _dbContext.CampSeasons.Add(season);
            await _dbContext.SaveChangesAsync();
        }

        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.CampMembers.Add(member);

        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = member.Id,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = userId,
        };
        _dbContext.CampRoleAssignments.Add(assignment);
        await _dbContext.SaveChangesAsync();
        return assignment;
    }
}

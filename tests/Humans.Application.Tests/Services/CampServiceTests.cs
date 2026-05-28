using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class CampServiceTests : ServiceTestHarness
{
    private readonly CampService _service;
    private readonly IUserService _userService;
    private readonly InMemoryFileStorage _fileStorage;
    private readonly ICampRoleService _campRoleService;

    public CampServiceTests()
        : base(Instant.FromUtc(2026, 3, 13, 12, 0))
    {
        _fileStorage = new InMemoryFileStorage();

        var repo = new CampRepository(DbFactory);

        _userService = NewDbBackedUserService();

        _campRoleService = Substitute.For<ICampRoleService>();
        _campRoleService.RemoveAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _service = new CampService(
            repo,
            _userService,
            AuditLog,
            Substitute.For<ISystemTeamSync>(),
            _fileStorage,
            Notifier,
            Substitute.For<ICampLeadJoinRequestsBadgeCacheInvalidator>(),
            new Lazy<ICampRoleService>(() => _campRoleService),
            Substitute.For<IEarlyEntryInvalidator>(),
            Clock,
            NullLogger<CampService>.Instance);
    }

    // ==========================================================================
    // CreateCampAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateCampAsync_NewCamp_CreatesCampWithPendingSeason()
    {
        await SeedSettingsAsync();
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();

        var camp = await _service.CreateCampAsync(
            userId, "Camp Funhouse", "camp@fun.com", "+34612345678",
            "https://instagram.com/funhouse", null,
            isSwissCamp: false, timesAtNowhere: 0,
            MakeSeasonData(), historicalNames: null, year: 2026);

        camp.Slug.Should().Be("camp-funhouse");
        camp.CreatedByUserId.Should().Be(userId);

        var season = await Db.CampSeasons.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CampId == camp.Id);
        season.Should().NotBeNull();
        season.Status.Should().Be(CampSeasonStatus.Pending);
        season.Year.Should().Be(2026);
        season.Name.Should().Be("Camp Funhouse");

        var member = await Db.CampMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CampSeasonId == season.Id && m.UserId == userId);
        member.Should().NotBeNull();
        member!.Status.Should().Be(CampMemberStatus.Active);

        var assignment = await Db.CampRoleAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CampMemberId == member.Id);
        assignment.Should().NotBeNull();
        assignment!.CampRoleDefinitionId.Should().Be(leadDef.Id);
    }

    [HumansFact]
    public async Task CreateCampAsync_NoLeadDefinition_CreatesCampAndActiveMemberWithoutAssignment()
    {
        await SeedSettingsAsync();
        var userId = Guid.NewGuid();

        var camp = await _service.CreateCampAsync(
            userId, "Camp Seedless", "c@s.com", "+34600000001", null, null,
            false, 0, MakeSeasonData(), null, 2026);

        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        (await Db.CampMembers.AsNoTracking().AnyAsync(m => m.CampSeasonId == season.Id && m.UserId == userId))
            .Should().BeTrue();
        (await Db.CampRoleAssignments.AsNoTracking().AnyAsync(a => a.CampSeasonId == season.Id))
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task CreateCampAsync_ReservedSlug_ThrowsInvalidOperation()
    {
        await SeedSettingsAsync();
        var userId = Guid.NewGuid();

        var act = () => _service.CreateCampAsync(
            userId, "Register", "camp@test.com", "+34600000000",
            null, null, false, 0, MakeSeasonData(), null, 2026);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved*");
    }

    // ==========================================================================
    // GetCampsForYearAsync role facts
    // ==========================================================================

    [HumansFact]
    public async Task GetCampsForYearAsync_RoleLeadResolvesSeason_NonLeadNull()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);

        var campInfo = (await _service.GetCampsForYearAsync(2026)).Single(c => c.Id == camp.Id);
        campInfo.GetLeadSeasonIdForYear(camp.CreatedByUserId, 2026)
            .Should().Be(season.Id);
        campInfo.GetLeadSeasonIdForYear(Guid.NewGuid(), 2026)
            .Should().BeNull();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_ProjectsSpecialRoleFacts()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();

        var info = (await _service.GetCampsForYearAsync(2026))
            .Single(c => c.Id == camp.Id);

        info.IsLead(camp.CreatedByUserId).Should().BeTrue();
        info.Active!.LeadUserIds.Should().Contain(camp.CreatedByUserId);
    }

    // ==========================================================================
    // ApproveSeasonAsync
    // ==========================================================================

    [HumansFact]
    public async Task ApproveSeasonAsync_PendingSeason_SetsActive()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        var adminId = Guid.NewGuid();

        await _service.ApproveSeasonAsync(season.Id, adminId, "Looks good");

        var updated = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        updated.Status.Should().Be(CampSeasonStatus.Active);
        updated.ReviewedByUserId.Should().Be(adminId);
        updated.ReviewNotes.Should().Be("Looks good");
        updated.ResolvedAt.Should().NotBeNull();
    }

    // ==========================================================================
    // RejectSeasonAsync
    // ==========================================================================

    [HumansFact]
    public async Task RejectSeasonAsync_PendingSeason_SetsRejected()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);

        await _service.RejectSeasonAsync(season.Id, Guid.NewGuid(), "Not a real camp");

        var updated = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        updated.Status.Should().Be(CampSeasonStatus.Rejected);
        updated.ReviewNotes.Should().Be("Not a real camp");
    }

    // ==========================================================================
    // OptInToSeasonAsync
    // ==========================================================================

    [HumansFact]
    public async Task OptInToSeasonAsync_ReturningCamp_AutoApproves()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        // Open 2027 season in settings
        var settings = await Db.CampSettings.FirstAsync();
        settings.OpenSeasons = [2026, 2027];
        await Db.SaveChangesAsync();

        var newSeason = await _service.OptInToSeasonAsync(camp.Id, 2027);

        newSeason.Status.Should().Be(CampSeasonStatus.Active);
        newSeason.Year.Should().Be(2027);
        newSeason.BlurbLong.Should().Be("A fun camp for everyone"); // copied
    }

    [HumansFact]
    public async Task OptInToSeasonAsync_PreviouslyRejected_GoesPending()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.RejectSeasonAsync(season.Id, Guid.NewGuid(), "nope");

        var settings = await Db.CampSettings.FirstAsync();
        settings.OpenSeasons = [2026, 2027];
        await Db.SaveChangesAsync();

        var newSeason = await _service.OptInToSeasonAsync(camp.Id, 2027);

        newSeason.Status.Should().Be(CampSeasonStatus.Pending);
    }

    [HumansFact]
    public async Task OptInToSeasonAsync_PendingOnly_GoesPending()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        // Don't approve or reject — season stays Pending

        var settings = await Db.CampSettings.FirstAsync();
        settings.OpenSeasons = [2026, 2027];
        await Db.SaveChangesAsync();

        var newSeason = await _service.OptInToSeasonAsync(camp.Id, 2027);

        newSeason.Status.Should().Be(CampSeasonStatus.Pending);
    }

    // ==========================================================================
    // GetCampsForYearAsync lead role facts
    // ==========================================================================

    [HumansFact]
    public async Task GetCampsForYearAsync_LeadRole_CanBeFilteredFromCampInfo()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var leadUserId = camp.CreatedByUserId;

        var result = (await _service.GetCampsForYearAsync(2026))
            .Single(c => c.Id == camp.Id)
            .IsLead(leadUserId);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_NonLead_FilterReturnsFalse()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var userId = Guid.NewGuid();
        var result = (await _service.GetCampsForYearAsync(2026))
            .Single(c => c.Id == camp.Id)
            .IsLead(userId);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ExportsLegacyCampLeadRows_UntilTableDrops()
    {
        // §8a GDPR completeness: the legacy camp_leads table still holds per-user
        // rows until #774 drops it, so the Article 15 export must include them.
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var userId = Guid.NewGuid();
        Db.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync();

        var slices = await ((IUserDataContributor)_service).ContributeForUserAsync(userId, CancellationToken.None);

        var leadSlice = slices.Should()
            .ContainSingle(s => s.SectionName == GdprExportSections.CampLeadAssignments).Subject;
        ((System.Collections.IEnumerable)leadSlice.Data!).Cast<object>().Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_LegacyCampLeadRowOnly_DoesNotProjectLead()
    {
        // Locks the removal of the legacy CampLead fallback: a camp_leads row with
        // no matching CampRoleAssignment no longer confers lead status.
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var legacyUserId = Guid.NewGuid();
        Db.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            UserId = legacyUserId,
            Role = CampLeadRole.CoLead,
            JoinedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync();

        (await _service.GetCampsForYearAsync(2026))
            .Single(c => c.Id == camp.Id)
            .IsLead(legacyUserId)
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_RegularRoleHolder_IsNotEventManager()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var regularDef = await SeedRegularDefinitionAsync();
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, regularDef.Id, userId);

        var result = (await _service.GetCampsForYearAsync(2026))
            .Single(camp => camp.Id == campId)
            .IsEventManager(userId);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // GetCampsForYearAsync event manager role facts
    // ==========================================================================

    [HumansFact]
    public async Task GetCampsForYearAsync_EventManagerRoleAssignment_CanBeFilteredFromCampInfo()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, userId);

        var result = (await _service.GetCampsForYearAsync(2026))
            .Where(camp => camp.IsEventManager(userId))
            .ToList();

        result.Should().ContainSingle(c => c.Id == campId);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_WorkshopAssignment_CanBeFilteredFromCampInfo()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var workshopDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Workshop);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, workshopDef.Id, userId);

        var result = (await _service.GetCampsForYearAsync(2026))
            .Where(camp => camp.IsEventManager(userId))
            .ToList();

        result.Should().ContainSingle(c => c.Id == campId);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_CreatorLead_CanBeFilteredFromCampInfo()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp(); // creator is seeded as a role-backed Camp Lead.

        var result = (await _service.GetCampsForYearAsync(2026))
            .Where(info => info.IsEventManager(camp.CreatedByUserId))
            .ToList();

        result.Should().ContainSingle(c => c.Id == camp.Id);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_RoleAndLegacyLead_CanBeFilteredOnceFromCampInfo()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp(); // creator is a role-backed Camp Lead.
        var seasonId = (await Db.CampSeasons
            .Where(s => s.CampId == camp.Id)
            .OrderByDescending(s => s.Year)
            .FirstAsync()).Id;
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, camp.CreatedByUserId);

        var result = (await _service.GetCampsForYearAsync(2026))
            .Where(info => info.IsEventManager(camp.CreatedByUserId))
            .ToList();

        result.Should().ContainSingle(c => c.Id == camp.Id);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_EventManagerRoleWithoutSeasonInYear_IsExcluded()
    {
        await SeedSettingsAsync();
        var (_, seasonId) = await SeedCampWithSeasonAsync(); // season is 2026.
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, userId);

        var result = (await _service.GetCampsForYearAsync(2027))
            .Where(camp => camp.IsEventManager(userId))
            .ToList();

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_NonEventManager_FilterReturnsEmpty()
    {
        await SeedSettingsAsync();
        await SeedCampWithSeasonAsync();

        var userId = Guid.NewGuid();
        var result = (await _service.GetCampsForYearAsync(2026))
            .Where(camp => camp.IsEventManager(userId))
            .ToList();

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // Public projections
    // ==========================================================================

    [HumansFact]
    public async Task GetCampsForYearAsync_ProjectsPublicApiFacts()
    {
        await SeedSettingsAsync();

        var zebraCamp = await _service.CreateCampAsync(
            Guid.NewGuid(),
            "Zebra Camp",
            "zebra@camp.com",
            "+34600000000",
            null,
            [new CampLink { Url = "https://example.com/zebra", Platform = "Website" }],
            isSwissCamp: true,
            timesAtNowhere: 3,
            MakeSeasonData() with
            {
                BlurbShort = "Zebra short",
                BlurbLong = "Zebra long",
                AcceptingMembers = YesNoMaybe.Maybe,
                KidsWelcome = YesNoMaybe.Yes,
                SoundZone = SoundZone.Red,
                Vibes = [CampVibe.LiveMusic]
            },
            historicalNames: null,
            year: 2026);

        var alphaCamp = await _service.CreateCampAsync(
            Guid.NewGuid(),
            "Alpha Camp",
            "alpha@camp.com",
            "+34600000001",
            "https://example.com/alpha",
            null,
            isSwissCamp: false,
            timesAtNowhere: 1,
            MakeSeasonData() with
            {
                BlurbShort = "Alpha short",
                BlurbLong = "Alpha long",
                AcceptingMembers = YesNoMaybe.Yes,
                KidsWelcome = YesNoMaybe.No,
                SoundZone = SoundZone.Green,
                Vibes = [CampVibe.ChillOut]
            },
            historicalNames: null,
            year: 2026);

        await ApproveLatestSeasonAsync(zebraCamp.Id);
        await ApproveLatestSeasonAsync(alphaCamp.Id);

        Db.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = zebraCamp.Id,
            StoragePath = "uploads/camps/zebra.jpg",
            SortOrder = 1,
            UploadedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

        var summaries = (await _service.GetCampsForYearAsync(2026))
            .Select(camp => new
            {
                Camp = camp,
                Season = camp.Seasons.Single(season => season.Year == 2026)
            })
            .OrderBy(row => row.Season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        summaries.Select(row => row.Season.Name).Should().Equal("Alpha Camp", "Zebra Camp");
        summaries[0].Season.BlurbShort.Should().Be("Alpha short");
        summaries[0].Season.BlurbLong.Should().Be("Alpha long");
        summaries[0].Season.Status.Should().Be(CampSeasonStatus.Active);
        summaries[0].Camp.WebOrSocialUrl.Should().Be("https://example.com/alpha");
        summaries[1].Camp.Images.Should().ContainSingle(image => image.Url == "/uploads/camps/zebra.jpg");
        summaries[1].Camp.Links.Should().ContainSingle();
        summaries[1].Season.AcceptingMembers.Should().Be(YesNoMaybe.Maybe);
        summaries[1].Season.KidsWelcome.Should().Be(YesNoMaybe.Yes);
        summaries[1].Season.SoundZone.Should().Be(SoundZone.Red);
        summaries[1].Season.Vibes.Should().Equal(CampVibe.LiveMusic);
        summaries[1].Camp.IsSwissCamp.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_ProjectsPlacementFacts()
    {
        await SeedSettingsAsync();

        var bravoCamp = await _service.CreateCampAsync(
            Guid.NewGuid(),
            "Bravo Camp",
            "bravo@camp.com",
            "+34600000002",
            null,
            null,
            isSwissCamp: false,
            timesAtNowhere: 0,
            MakeSeasonData() with
            {
                MemberCount = 42,
                SpaceRequirement = SpaceSize.Sqm800,
                SoundZone = SoundZone.Blue,
                ElectricalGrid = ElectricalGrid.Red
            },
            historicalNames: null,
            year: 2026);

        var alphaCamp = await _service.CreateCampAsync(
            Guid.NewGuid(),
            "Alpha Camp",
            "alpha2@camp.com",
            "+34600000003",
            null,
            null,
            isSwissCamp: false,
            timesAtNowhere: 0,
            MakeSeasonData() with
            {
                MemberCount = 10,
                SpaceRequirement = SpaceSize.Sqm300,
                SoundZone = SoundZone.Green,
                ElectricalGrid = ElectricalGrid.Yellow
            },
            historicalNames: null,
            year: 2026);

        await ApproveLatestSeasonAsync(bravoCamp.Id);
        await ApproveLatestSeasonAsync(alphaCamp.Id);

        var placements = (await _service.GetCampsForYearAsync(2026))
            .Select(camp => new
            {
                Camp = camp,
                Season = camp.Seasons.Single(season => season.Year == 2026)
            })
            .OrderBy(row => row.Season.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        placements.Select(row => row.Season.Name).Should().Equal("Alpha Camp", "Bravo Camp");
        placements[1].Season.MemberCount.Should().Be(42);
        placements[1].Season.SpaceRequirement.Should().Be(SpaceSize.Sqm800);
        placements[1].Season.SoundZone.Should().Be(SoundZone.Blue);
        placements[1].Season.ElectricalGrid.Should().Be(ElectricalGrid.Red);
        placements[1].Season.Status.Should().Be(CampSeasonStatus.Active);
    }

    [HumansFact]
    public async Task GetCampBySlugAsync_ProjectsDetailPageFacts()
    {
        await SeedSettingsAsync();
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "Camp Lead");

        var camp = await _service.CreateCampAsync(
            leadUserId,
            "Fallback Camp",
            "fallback@camp.com",
            "+34600000004",
            "https://example.com/fallback",
            null,
            isSwissCamp: true,
            timesAtNowhere: 4,
            MakeSeasonData(),
            historicalNames: ["Old Fallback"],
            year: 2026);

        await ApproveLatestSeasonAsync(camp.Id);

        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        season.NameLockDate = new LocalDate(2026, 3, 1);

        Db.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            StoragePath = "uploads/camps/fallback.jpg",
            SortOrder = 1,
            UploadedAt = Clock.GetCurrentInstant()
        });

        await Db.SaveChangesAsync();

        var detail = await _service.GetCampBySlugAsync(camp.Slug);

        detail.Should().NotBeNull();
        detail.Slug.Should().Be(camp.Slug);
        detail.WebOrSocialUrl.Should().Be("https://example.com/fallback");
        detail.HistoricalNames.Should().Contain("Old Fallback");
        detail.Images.Should().ContainSingle(image => image.Url == "/uploads/camps/fallback.jpg");
        detail.HideHistoricalNames.Should().BeFalse();

        var seasonInfo = detail.GetSeasonForYear(2027, fallbackToLatestSeason: true);
        seasonInfo.Should().NotBeNull();
        seasonInfo!.Year.Should().Be(2026);
        seasonInfo.IsNameLocked(new LocalDate(2026, 3, 2)).Should().BeTrue();
        seasonInfo.KidsVisiting.Should().Be(KidsVisitingPolicy.DaytimeOnly);
        seasonInfo.HasPerformanceSpace.Should().Be(PerformanceSpaceStatus.Yes);
    }

    [HumansFact]
    public async Task CampInfo_GetSeasonForYearWithoutFallback_ReturnsNullWhenSeasonMissing()
    {
        await SeedSettingsAsync();
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "No Fallback Lead");

        var camp = await _service.CreateCampAsync(
            leadUserId,
            "No Fallback Camp",
            "nofollow@camp.com",
            "+34600000005",
            null,
            null,
            isSwissCamp: false,
            timesAtNowhere: 0,
            MakeSeasonData(),
            historicalNames: null,
            year: 2026);

        await ApproveLatestSeasonAsync(camp.Id);

        var detail = await _service.GetCampBySlugAsync(camp.Slug);

        detail.Should().NotBeNull();
        detail!.GetSeasonForYear(2027).Should().BeNull();
    }

    [HumansFact]
    public async Task GetSettingsAsync_AfterSetPublicYearAsync_ReturnsInvalidatedSettings()
    {
        await SeedSettingsAsync();

        var initial = await _service.GetSettingsAsync();

        initial.PublicYear.Should().Be(2026);

        await _service.SetPublicYearAsync(2027);

        var updated = await _service.GetSettingsAsync();

        updated.PublicYear.Should().Be(2027);
    }

    [HumansFact]
    public async Task GetSettingsAsync_IncludesOpenSeasonNameLockDates()
    {
        await SeedSettingsAsync();
        await CreateTestCamp();
        var lockDate = new LocalDate(2026, 3, 1);

        await _service.SetNameLockDateAsync(2026, lockDate);

        var settings = await _service.GetSettingsAsync();

        settings.NameLockDates.Should().ContainKey(2026);
        settings.NameLockDates[2026].Should().Be(lockDate);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_AfterUploadImageAsync_RefreshesImageFacts()
    {
        await SeedSettingsAsync();

        var camp = await _service.CreateCampAsync(
            Guid.NewGuid(),
            "Cache Image Camp",
            "cache-image@camp.com",
            "+34600000006",
            null,
            null,
            isSwissCamp: false,
            timesAtNowhere: 0,
            MakeSeasonData(),
            historicalNames: null,
            year: 2026);

        await ApproveLatestSeasonAsync(camp.Id);

        var beforeUpload = await _service.GetCampsForYearAsync(2026);

        beforeUpload.Single(info => info.Id == camp.Id).Images.Should().BeEmpty();

        await using var imageStream = new MemoryStream([1, 2, 3, 4]);
        var uploadResult = await _service.UploadImageAsync(camp.Id, imageStream, "camp.jpg", "image/jpeg", imageStream.Length);

        uploadResult.Succeeded.Should().BeTrue();

        var afterUpload = await _service.GetCampsForYearAsync(2026);

        afterUpload.Single(info => info.Id == camp.Id).Images.Should().NotBeEmpty();
    }

    // ==========================================================================
    // ChangeSeasonNameAsync
    // ==========================================================================

    [HumansFact(Timeout = 10000)]
    public async Task ChangeSeasonNameAsync_LogsOldNameToHistory()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        await _service.ChangeSeasonNameAsync(season.Id, "New Name");

        var updated = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        updated.Name.Should().Be("New Name");

        var historical = await Db.CampHistoricalNames
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.CampId == camp.Id && h.Source == CampNameSource.NameChange);
        historical.Should().NotBeNull();
        historical.Name.Should().Be("Test Camp");
    }

    [HumansFact]
    public async Task ChangeSeasonNameAsync_AfterLockDate_Throws()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        // Set lock date in the past
        season.NameLockDate = new LocalDate(2026, 3, 1);
        await Db.SaveChangesAsync();

        var act = () => _service.ChangeSeasonNameAsync(season.Id, "Too Late");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked*");
    }

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    [HumansFact]
    public async Task RequestCampMembershipAsync_OpenSeason_CreatesPending()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");

        var result = await _service.RequestCampMembershipAsync(camp.Id, userId);

        result.Outcome.Should().Be(CampMemberRequestOutcome.Created);
        result.NoticeLevel.Should().Be(CampMemberRequestNoticeLevel.Success);
        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == result.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Pending);
        member.UserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task RequestCampMembershipAsync_NoOpenSeason_ReturnsNoOpenSeason()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");

        var result = await _service.RequestCampMembershipAsync(camp.Id, userId);

        result.Outcome.Should().Be(CampMemberRequestOutcome.NoOpenSeason);
        result.Message.Should().Be("Camp is not open for membership this year.");
        result.NoticeLevel.Should().Be(CampMemberRequestNoticeLevel.Error);
        // The creator is an Active member; assert no row was created for the requester.
        (await Db.CampMembers.AsNoTracking().AnyAsync(m => m.UserId == userId)).Should().BeFalse();
    }

    [HumansFact]
    public async Task RequestCampMembershipAsync_AlreadyPending_IsIdempotent()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");

        var first = await _service.RequestCampMembershipAsync(camp.Id, userId);
        var second = await _service.RequestCampMembershipAsync(camp.Id, userId);

        second.Outcome.Should().Be(CampMemberRequestOutcome.AlreadyPending);
        second.NoticeLevel.Should().Be(CampMemberRequestNoticeLevel.Info);
        second.CampMemberId.Should().Be(first.CampMemberId);
        // Scope to the requester — the camp creator also has an Active member row.
        (await Db.CampMembers.AsNoTracking().CountAsync(m => m.UserId == userId)).Should().Be(1);
    }

    [HumansFact]
    public async Task ApproveCampMemberAsync_PendingRequest_SetsActiveAndNotifies()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        var approverId = Guid.NewGuid();

        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, approverId);

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedByUserId.Should().Be(approverId);
        member.ConfirmedAt.Should().NotBeNull();

        await Notifier.Received(1).SendAsync(
            NotificationSource.CampMembershipApproved,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == userId),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApproveCampMemberAsync_CrossCampMemberId_Throws()
    {
        // P1 fix: controller authorizes camp A, but attacker submits camp B's member id.
        await SeedSettingsAsync();
        var campA = await CreateTestCamp();
        await ApproveLatestSeasonAsync(campA.Id);
        var campB = await _service.CreateCampAsync(
            Guid.NewGuid(), "Other Camp", "other@camp.com", "+34600000001",
            null, null, false, 1, MakeSeasonData(), null, 2026);
        await ApproveLatestSeasonAsync(campB.Id);

        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var requestInCampB = await _service.RequestCampMembershipAsync(campB.Id, userId);

        // A lead of camp A tries to approve a member belonging to camp B.
        var act = () => _service.ApproveCampMemberAsync(campA.Id, requestInCampB.CampMemberId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");

        // Camp B's pending row is untouched.
        var memberB = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == requestInCampB.CampMemberId);
        memberB.Status.Should().Be(CampMemberStatus.Pending);
    }

    [HumansFact]
    public async Task RejectCampMemberAsync_PendingRequest_SetsRemovedAndNotifies()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);

        await _service.RejectCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
        member.RemovedAt.Should().NotBeNull();

        await Notifier.Received(1).SendAsync(
            NotificationSource.CampMembershipRejected,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == userId),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectCampMemberAsync_AllowsReRequest()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var first = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.RejectCampMemberAsync(camp.Id, first.CampMemberId, Guid.NewGuid());

        var second = await _service.RequestCampMembershipAsync(camp.Id, userId);

        second.Outcome.Should().Be(CampMemberRequestOutcome.Created);
        second.CampMemberId.Should().NotBe(first.CampMemberId);
    }

    [HumansFact]
    public async Task WithdrawCampMembershipRequestAsync_Self_SetsRemoved()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);

        await _service.WithdrawCampMembershipRequestAsync(request.CampMemberId, userId);

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [HumansFact]
    public async Task WithdrawCampMembershipRequestAsync_OtherUser_Throws()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);

        var act = () => _service.WithdrawCampMembershipRequestAsync(request.CampMemberId, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [HumansFact]
    public async Task LeaveCampAsync_ActiveMember_SetsRemoved()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var result = await _service.LeaveCampAsync(request.CampMemberId, userId);

        result.Succeeded.Should().BeTrue();

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [HumansFact]
    public async Task RemoveCampMemberAsync_ActiveMember_SetsRemoved()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        await _service.RemoveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [HumansFact]
    public async Task AddMemberAndAssignRoleInActiveSeason_creates_active_member_assigns_role_and_audits()
    {
        // Seed a camp + season + a target user
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "test-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var roleDefinitionId = Guid.NewGuid();
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();
        _campRoleService.AssignAsync(
                season.Id, roleDefinitionId, Arg.Any<Guid>(), actorUserId, Arg.Any<CancellationToken>())
            .Returns(AssignCampRoleOutcome.Assigned);

        var result = await _service.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, roleDefinitionId, targetUserId, actorUserId);

        result.Should().Be(AssignCampRoleOutcome.Assigned);
        var member = await Db.CampMembers.AsNoTracking().SingleAsync(m => m.UserId == targetUserId);
        var memberId = member.Id;
        memberId.Should().NotBe(Guid.Empty);
        member.CampSeasonId.Should().Be(season.Id);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedByUserId.Should().Be(actorUserId);

        await _campRoleService.Received(1).AssignAsync(
            season.Id, roleDefinitionId, memberId, actorUserId, Arg.Any<CancellationToken>());

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampMemberAddedByLead,
            nameof(CampMember), memberId,
            Arg.Any<string>(), actorUserId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddCampMemberToActiveSeason_creates_active_member_and_audits()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "active-add-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();

        var result = await _service.AddCampMemberToActiveSeasonAsync(camp.Id, targetUserId, actorUserId);

        result.Should().Be(AddCampMemberOutcome.Added);
        var member = await Db.CampMembers.AsNoTracking().SingleAsync(m => m.UserId == targetUserId);
        member.CampSeasonId.Should().Be(season.Id);
        member.UserId.Should().Be(targetUserId);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedByUserId.Should().Be(actorUserId);

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampMemberAddedByLead,
            nameof(CampMember), member.Id,
            Arg.Any<string>(), actorUserId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddMemberAndAssignRoleInActiveSeason_uses_existing_active_member_without_audit()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "test-camp-2" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var userId = Guid.NewGuid();
        var existing = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
            ConfirmedByUserId = Guid.NewGuid(),
        };
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        Db.CampMembers.Add(existing);
        await Db.SaveChangesAsync();

        var actorUserId = Guid.NewGuid();
        var roleDefinitionId = Guid.NewGuid();
        _campRoleService.AssignAsync(
                season.Id, roleDefinitionId, existing.Id, actorUserId, Arg.Any<CancellationToken>())
            .Returns(AssignCampRoleOutcome.Assigned);

        var result = await _service.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, roleDefinitionId, userId, actorUserId);

        result.Should().Be(AssignCampRoleOutcome.Assigned);
        await _campRoleService.Received(1).AssignAsync(
            season.Id, roleDefinitionId, existing.Id, actorUserId, Arg.Any<CancellationToken>());
        // No new audit log for an already-active member.
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.CampMemberAddedByLead, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddMemberAndAssignRoleInActiveSeason_uses_active_season()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "active-camp" };
        var inactiveSeason = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2025, Status = CampSeasonStatus.Pending };
        var activeSeason = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var roleDefinitionId = Guid.NewGuid();
        Db.Camps.Add(camp);
        await Db.CampSeasons.AddRangeAsync(inactiveSeason, activeSeason);
        await Db.SaveChangesAsync();
        _campRoleService.AssignAsync(
                activeSeason.Id, roleDefinitionId, Arg.Any<Guid>(), actorUserId, Arg.Any<CancellationToken>())
            .Returns(AssignCampRoleOutcome.Assigned);

        var result = await _service.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, roleDefinitionId, targetUserId, actorUserId);

        result.Should().Be(AssignCampRoleOutcome.Assigned);
        var member = await Db.CampMembers.AsNoTracking().SingleAsync(m => m.UserId == targetUserId);
        member.CampSeasonId.Should().Be(activeSeason.Id);
        await _campRoleService.Received(1).AssignAsync(
            activeSeason.Id, roleDefinitionId, member.Id, actorUserId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AddMemberAndAssignRoleInActiveSeason_without_active_season_does_not_create_member()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "inactive-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Pending };
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();

        var result = await _service.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(AssignCampRoleOutcome.SeasonNotFound);
        (await Db.CampMembers.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task AddMemberAndAssignRoleInActiveSeason_without_active_season_returns_season_not_found()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "inactive-role-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Pending };
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();

        var result = await _service.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(AssignCampRoleOutcome.SeasonNotFound);
    }

    [HumansFact]
    public async Task LeaveCamp_cascades_role_assignment_cleanup()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "leave-cascade" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var userId = Guid.NewGuid();
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
            ConfirmedByUserId = Guid.NewGuid(),
        };
        Db.Camps.Add(camp); Db.CampSeasons.Add(season); Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();

        var result = await _service.LeaveCampAsync(member.Id, userId);

        result.Succeeded.Should().BeTrue();

        await _campRoleService.Received(1).RemoveAllForMemberAsync(
            member.Id, userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task WithdrawCampMembershipRequest_cascades_role_assignment_cleanup()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "withdraw-cascade" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var userId = Guid.NewGuid();
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = userId,
            Status = CampMemberStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant(),
        };
        Db.Camps.Add(camp); Db.CampSeasons.Add(season); Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();

        await _service.WithdrawCampMembershipRequestAsync(member.Id, userId);

        await _campRoleService.Received(1).RemoveAllForMemberAsync(
            member.Id, userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveCampMemberAsync_CascadesRoleAssignments()
    {
        // Bug fix bundled with issue-490: the section invariants doc says Remove
        // cascades role assignments, but the code historically did not. Route
        // through the new TransitionMemberToRemovedAsync helper closes that gap.
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "remove-cascade" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var memberId = Guid.NewGuid();
        var member = new CampMember
        {
            Id = memberId,
            CampSeasonId = season.Id,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
            ConfirmedByUserId = Guid.NewGuid(),
        };
        Db.Camps.Add(camp); Db.CampSeasons.Add(season); Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();

        await _service.RemoveCampMemberAsync(camp.Id, memberId, Guid.NewGuid());

        await _campRoleService.Received(1).RemoveAllForMemberAsync(
            memberId, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task WithdrawSeasonAsync_NotifiesPendingRequesters_DoesNotChangeMemberStatus()
    {
        // No more auto-withdraw cascade — just a notification out. Pending rows stay pending.
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        Notifier.ClearReceivedCalls();

        await _service.WithdrawSeasonAsync(season.Id);

        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Pending);

        await Notifier.Received(1).SendAsync(
            NotificationSource.CampMembershipSeasonClosed,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == userId),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_ProjectsSeasonMembersAndMembershipState()
    {
        await SeedSettingsAsync();
        await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "Lead Larry");
        var camp = await _service.CreateCampAsync(
            leadUserId, "Lead Camp", "lc@camp.com", "+34600000010",
            null, null, false, 1, MakeSeasonData(), null, 2026);
        await ApproveLatestSeasonAsync(camp.Id);

        var memberUserId = Guid.NewGuid();
        await SeedUserAsync(memberUserId, "Member Mary");
        var request = await _service.RequestCampMembershipAsync(camp.Id, memberUserId);

        var pendingCamp = (await _service.GetCampsForYearAsync(2026)).Single(c => c.Id == camp.Id);
        pendingCamp.CurrentSeason.Should().NotBeNull();
        var pendingSeason = pendingCamp.CurrentSeason!;

        pendingSeason.PendingMembers.Should()
            .ContainSingle(m => m.UserId == memberUserId && m.Id == request.CampMemberId);
        pendingCamp.GetMembershipState(memberUserId).Status.Should().Be(CampMemberStatusSummary.Pending);

        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var activeCamp = (await _service.GetCampsForYearAsync(2026)).Single(c => c.Id == camp.Id);
        activeCamp.CurrentSeason.Should().NotBeNull();
        var activeSeason = activeCamp.CurrentSeason!;

        activeSeason.ActiveMembers.Should().Contain(m => m.UserId == memberUserId && m.Id == request.CampMemberId);
        activeSeason.ActiveMembers.Should().ContainSingle(m => m.UserId == leadUserId);
        activeCamp.GetMembershipState(memberUserId).Status.Should().Be(CampMemberStatusSummary.Active);
        activeCamp.GetMembershipState(memberUserId).OpenSeasonYear.Should().Be(2026);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_NoOpenSeasonMembershipState_ReturnsNoOpenSeason()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();

        var campInfo = (await _service.GetCampsForYearAsync(2026)).Single(c => c.Id == camp.Id);

        campInfo.GetMembershipState(Guid.NewGuid()).Status.Should().Be(CampMemberStatusSummary.NoOpenSeason);
    }

    [HumansFact]
    public void CampInfo_GetMembershipState_UsesLatestOpenSeason_WhenFuturePendingSeasonExists()
    {
        var campId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var memberUserId = Guid.NewGuid();
        var requestedAt = Instant.FromUtc(2026, 3, 13, 12, 0);
        var activeSeason = MakeCampSeasonInfo(campId, 2026, CampSeasonStatus.Active) with
        {
            Members =
            [
                new CampSeasonMemberInfo(
                    memberId,
                    memberUserId,
                    CampMemberStatus.Active,
                    requestedAt,
                    requestedAt,
                    HasEarlyEntry: false)
            ]
        };
        var pendingFutureSeason = MakeCampSeasonInfo(campId, 2027, CampSeasonStatus.Pending);
        var campInfo = new CampInfo(
            campId,
            "future-pending",
            "camp@example.com",
            "+34600000010",
            IsSwissCamp: false,
            TimesAtNowhere: 1,
            [activeSeason, pendingFutureSeason]);

        var state = campInfo.GetMembershipState(memberUserId);

        state.Status.Should().Be(CampMemberStatusSummary.Active);
        state.OpenSeasonYear.Should().Be(2026);
        state.OpenSeasonId.Should().Be(activeSeason.Id);
        state.CampMemberId.Should().Be(memberId);
    }

    [HumansFact]
    public async Task GetCampsForYearAsync_ReturnsOnlyRealCampMemberRows_AfterCampLeadRetirement()
    {
        // Issue nobodies-collective/Humans#753: the IsLead union into the
        // active-members list was removed. Active members come only from real
        // CampMember rows — no synthesis. The camp creator is now a real Active
        // member (+ Camp Lead role assignment), so they appear via their own row,
        // not a synthesized one.
        await SeedSettingsAsync();
        await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "Lead Larry");
        var camp = await _service.CreateCampAsync(
            leadUserId, "Lead Camp", "lc@camp.com", "+34600000010",
            null, null, false, 1, MakeSeasonData(), null, 2026);
        await ApproveLatestSeasonAsync(camp.Id);

        var memberUserId = Guid.NewGuid();
        await SeedUserAsync(memberUserId, "Member Mary");
        var req = await _service.RequestCampMembershipAsync(camp.Id, memberUserId);
        await _service.ApproveCampMemberAsync(camp.Id, req.CampMemberId, Guid.NewGuid());

        var projectedCamp = (await _service.GetCampsForYearAsync(2026)).Single(c => c.Id == camp.Id);
        projectedCamp.CurrentSeason.Should().NotBeNull();
        var members = projectedCamp.CurrentSeason!.ActiveMembers;

        // Two real Active members: the creator-lead (real row) + the approved member.
        members.Should().HaveCount(2);
        members.Should().Contain(r => r.UserId == memberUserId && r.Id == req.CampMemberId);
        var creatorRow = members.Should().ContainSingle(r => r.UserId == leadUserId).Subject;
        creatorRow.Id.Should().NotBe(Guid.Empty);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static CampSeasonData MakeSeasonData() => new(
        BlurbLong: "A fun camp for everyone",
        BlurbShort: "Fun camp",
        Languages: "English, Spanish",
        AcceptingMembers: YesNoMaybe.Yes,
        KidsWelcome: YesNoMaybe.Maybe,
        KidsVisiting: KidsVisitingPolicy.DaytimeOnly,
        KidsAreaDescription: null,
        HasPerformanceSpace: PerformanceSpaceStatus.Yes,
        PerformanceTypes: "Music, dance",
        Vibes: [CampVibe.LiveMusic, CampVibe.ChillOut],
        AdultPlayspace: AdultPlayspacePolicy.No,
        MemberCount: 25,
        SpaceRequirement: SpaceSize.Sqm600,
        SoundZone: SoundZone.Yellow,
        ElectricalGrid: ElectricalGrid.Yellow);

    private static CampSeasonInfo MakeCampSeasonInfo(Guid campId, int year, CampSeasonStatus status) =>
        new(
            Guid.NewGuid(),
            campId,
            "future-pending",
            year,
            NameLockDate: null,
            $"Future Pending {year}",
            "Short blurb",
            "English",
            [CampVibe.ChillOut],
            status,
            YesNoMaybe.Yes,
            YesNoMaybe.Yes,
            AdultPlayspacePolicy.No,
            MemberCount: 10,
            SoundZone.Blue,
            SpaceSize.Sqm600,
            ElectricalGrid.Yellow,
            EeSlotCount: 0,
            EeGrantedCount: 0,
            JoinedMemberCount: 0);

    private async Task<Camp> CreateTestCamp()
    {
        // Production seeds the Camp Lead special role in every env, so creation
        // produces a role-based lead. Mirror that here (idempotent) so the
        // creator is an Active member + Camp Lead assignment.
        if (!await Db.CampRoleDefinitions.AnyAsync(d => d.SpecialRole == CampSpecialRole.Lead))
        {
            await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        }

        return await _service.CreateCampAsync(
            Guid.NewGuid(), "Test Camp", "test@camp.com", "+34600000000",
            null, null, false, 1, MakeSeasonData(), null, 2026);
    }

    private async Task ApproveLatestSeasonAsync(Guid campId)
    {
        var season = await Db.CampSeasons
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year)
            .FirstAsync();

        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);
    }

    private async Task SeedSettingsAsync()
    {
        if (!await Db.CampSettings.AnyAsync())
        {
            Db.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = 2026,
                OpenSeasons = [2026]
            });
            await Db.SaveChangesAsync();
        }
    }

    private async Task SeedUserAsync(Guid userId, string displayName)
    {
        Db.Users.Add(new User
        {
            Id = userId,
            UserName = $"{displayName.Replace(" ", string.Empty, StringComparison.Ordinal)}@example.com",
            Email = $"{displayName.Replace(" ", string.Empty, StringComparison.Ordinal)}@example.com",
            DisplayName = displayName
        });

        await Db.SaveChangesAsync();
    }

    private async Task<(Guid CampId, Guid SeasonId)> SeedCampWithSeasonAsync()
    {
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons
            .Where(s => s.CampId == camp.Id)
            .OrderByDescending(s => s.Year)
            .FirstAsync();
        return (camp.Id, season.Id);
    }

    private async Task<CampRoleDefinition> SeedSpecialDefinitionAsync(CampSpecialRole specialRole)
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"{specialRole} Test",
            Slug = $"{specialRole.ToString().ToLowerInvariant()}-test-{Guid.NewGuid():N}".Substring(0, 30),
            SlotCount = 2,
            MinimumRequired = 0,
            SortOrder = 0,
            SpecialRole = specialRole,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.CampRoleDefinitions.Add(def);
        await Db.SaveChangesAsync();
        return def;
    }

    private async Task<CampRoleDefinition> SeedRegularDefinitionAsync()
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"Regular {Guid.NewGuid():N}".Substring(0, 20),
            Slug = $"regular-{Guid.NewGuid():N}".Substring(0, 20),
            SlotCount = 2,
            MinimumRequired = 0,
            SortOrder = 100,
            SpecialRole = CampSpecialRole.None,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.CampRoleDefinitions.Add(def);
        await Db.SaveChangesAsync();
        return def;
    }

    private async Task SeedRoleAssignmentAsync(Guid seasonId, Guid roleDefinitionId, Guid userId)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            UserId = userId,
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
        };
        Db.CampMembers.Add(member);

        Db.CampRoleAssignments.Add(new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = member.Id,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = userId,
        });

        await Db.SaveChangesAsync();
    }
}

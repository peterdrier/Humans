using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
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
        var roleRepo = new CampRoleRepository(DbFactory);

        _userService = NewDbBackedUserService();

        _campRoleService = Substitute.For<ICampRoleService>();
        _campRoleService.RemoveAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _service = new CampService(
            repo,
            roleRepo,
            _userService,
            AuditLog,
            Substitute.For<ISystemTeamSync>(),
            _fileStorage,
            Notifier,
            Substitute.For<ICampLeadJoinRequestsBadgeCacheInvalidator>(),
            new Lazy<ICampRoleService>(() => _campRoleService),
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
    // GetCampDirectoryAsync — role-based lead pinning
    // ==========================================================================

    [HumansFact]
    public async Task GetCampDirectoryAsync_RoleLeadPendingCamp_AppearsInMyCamps()
    {
        await SeedSettingsAsync();
        // CreateTestCamp seeds the Camp Lead role + makes the creator a role-based
        // lead; the new camp's season is Pending (not yet public).
        var camp = await CreateTestCamp();

        var result = await _service.GetCampDirectoryAsync(camp.CreatedByUserId);

        // Pending camps aren't in the public listing, but a role-lead sees theirs in MyCamps.
        result.MyCamps.Should().ContainSingle(c => c.Id == camp.Id);
        result.Camps.Should().NotContain(c => c.Id == camp.Id);
    }

    [HumansFact]
    public async Task GetCampLeadSeasonIdForYearAsync_RoleLeadResolvesSeason_NonLeadNull()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);

        (await _service.GetCampLeadSeasonIdForYearAsync(camp.CreatedByUserId, 2026))
            .Should().Be(season.Id);
        (await _service.GetCampLeadSeasonIdForYearAsync(Guid.NewGuid(), 2026))
            .Should().BeNull();
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
    // IsUserCampLeadAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserCampLeadAsync_ActiveLead_ReturnsTrue()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var leadUserId = camp.CreatedByUserId;

        var result = await _service.IsUserCampLeadAsync(leadUserId, camp.Id);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCampLeadAsync_NonLead_ReturnsFalse()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var result = await _service.IsUserCampLeadAsync(Guid.NewGuid(), camp.Id);
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
    public async Task IsUserCampLeadAsync_LegacyCampLeadRowOnly_ReturnsFalse()
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

        (await _service.IsUserCampLeadAsync(legacyUserId, camp.Id)).Should().BeFalse();
    }

    // ==========================================================================
    // IsUserCampEventManagerAsync (issue nobodies-collective/Humans#753)
    // ==========================================================================

    [HumansFact]
    public async Task IsUserCampEventManagerAsync_LeadAssignment_ReturnsTrue()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, userId);

        var result = await _service.IsUserCampEventManagerAsync(userId, campId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCampEventManagerAsync_WorkshopAssignment_ReturnsTrue()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var workshopDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Workshop);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, workshopDef.Id, userId);

        var result = await _service.IsUserCampEventManagerAsync(userId, campId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCampEventManagerAsync_RegularRoleHolder_ReturnsFalse()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var regularDef = await SeedRegularDefinitionAsync();
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, regularDef.Id, userId);

        var result = await _service.IsUserCampEventManagerAsync(userId, campId);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCampEventManagerAsync_NonMember_ReturnsFalse()
    {
        await SeedSettingsAsync();
        var (campId, _) = await SeedCampWithSeasonAsync();
        await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);

        var result = await _service.IsUserCampEventManagerAsync(Guid.NewGuid(), campId);

        result.Should().BeFalse();
    }

    // ==========================================================================
    // GetEventManagedCampsAsync — unions role-based (Lead/Workshop) holders with
    // the legacy CampLead table, then filters to camps with a season in the year.
    // ==========================================================================

    [HumansFact]
    public async Task GetEventManagedCampsAsync_RoleAssignmentOnly_ReturnsCamp()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, userId);

        var result = await _service.GetEventManagedCampsAsync(userId, 2026);

        result.Should().ContainSingle(c => c.Id == campId);
    }

    [HumansFact]
    public async Task GetEventManagedCampsAsync_WorkshopAssignment_ReturnsCamp()
    {
        await SeedSettingsAsync();
        var (campId, seasonId) = await SeedCampWithSeasonAsync();
        var workshopDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Workshop);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, workshopDef.Id, userId);

        var result = await _service.GetEventManagedCampsAsync(userId, 2026);

        result.Should().ContainSingle(c => c.Id == campId);
    }

    [HumansFact]
    public async Task GetEventManagedCampsAsync_LegacyLeadOnly_ReturnsCamp()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp(); // creator is seeded as a legacy CampLead only.

        var result = await _service.GetEventManagedCampsAsync(camp.CreatedByUserId, 2026);

        result.Should().ContainSingle(c => c.Id == camp.Id);
    }

    [HumansFact]
    public async Task GetEventManagedCampsAsync_RoleAndLegacyLead_ReturnsCampOnce()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp(); // creator is a legacy CampLead.
        var seasonId = (await Db.CampSeasons
            .Where(s => s.CampId == camp.Id)
            .OrderByDescending(s => s.Year)
            .FirstAsync()).Id;
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, camp.CreatedByUserId);

        var result = await _service.GetEventManagedCampsAsync(camp.CreatedByUserId, 2026);

        result.Should().ContainSingle(c => c.Id == camp.Id);
    }

    [HumansFact]
    public async Task GetEventManagedCampsAsync_RoleForCampWithoutSeasonInYear_Excluded()
    {
        await SeedSettingsAsync();
        var (_, seasonId) = await SeedCampWithSeasonAsync(); // season is 2026.
        var leadDef = await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var userId = Guid.NewGuid();
        await SeedRoleAssignmentAsync(seasonId, leadDef.Id, userId);

        var result = await _service.GetEventManagedCampsAsync(userId, 2027);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetEventManagedCampsAsync_NonManager_ReturnsEmpty()
    {
        await SeedSettingsAsync();
        await SeedCampWithSeasonAsync();

        var result = await _service.GetEventManagedCampsAsync(Guid.NewGuid(), 2026);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // Public projections
    // ==========================================================================

    [HumansFact]
    public async Task GetCampPublicSummariesForYearAsync_ReturnsSortedProjectedSummaries()
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

        var summaries = await _service.GetCampPublicSummariesForYearAsync(2026);

        summaries.Select(summary => summary.Name).Should().Equal("Alpha Camp", "Zebra Camp");
        summaries[0].BlurbShort.Should().Be("Alpha short");
        summaries[0].Status.Should().Be(nameof(CampSeasonStatus.Active));
        summaries[0].WebOrSocialUrl.Should().Be("https://example.com/alpha");
        summaries[1].ImageUrl.Should().Be("/uploads/camps/zebra.jpg");
        summaries[1].Links.Should().ContainSingle();
        summaries[1].AcceptingMembers.Should().Be(nameof(YesNoMaybe.Maybe));
        summaries[1].KidsWelcome.Should().Be(nameof(YesNoMaybe.Yes));
        summaries[1].SoundZone.Should().Be(nameof(SoundZone.Red));
        summaries[1].Vibes.Should().Equal(nameof(CampVibe.LiveMusic));
        summaries[1].IsSwissCamp.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCampPlacementSummariesForYearAsync_ReturnsSortedPlacementData()
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

        var placements = await _service.GetCampPlacementSummariesForYearAsync(2026);

        placements.Select(summary => summary.Name).Should().Equal("Alpha Camp", "Bravo Camp");
        placements[1].MemberCount.Should().Be(42);
        placements[1].SpaceRequirement.Should().Be(nameof(SpaceSize.Sqm800));
        placements[1].SoundZone.Should().Be(nameof(SoundZone.Blue));
        placements[1].ElectricalGrid.Should().Be(nameof(ElectricalGrid.Red));
        placements[1].Status.Should().Be(nameof(CampSeasonStatus.Active));
    }

    [HumansFact]
    public async Task GetCampDetailAsync_UsesPublicYearAndFallsBackToLatestSeason()
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

        var settings = await Db.CampSettings.FirstAsync();
        settings.PublicYear = 2027;
        await Db.SaveChangesAsync();

        var detail = await _service.BuildCampDetailDataBySlugAsync(camp.Slug);

        detail.Should().NotBeNull();
        detail.Name.Should().Be("Fallback Camp");
        detail.Links.Should().ContainSingle(link => link.Url == "https://example.com/fallback");
        detail.HistoricalNames.Should().Contain("Old Fallback");
        detail.ImageUrls.Should().ContainSingle("/uploads/camps/fallback.jpg");
        detail.CurrentSeason.Should().NotBeNull();
        detail.CurrentSeason!.Year.Should().Be(2026);
        detail.CurrentSeason.IsNameLocked.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetCampDetailAsync_ExplicitYearWithoutFallback_ReturnsNullWhenSeasonMissing()
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

        var detail = await _service.BuildCampDetailDataBySlugAsync(
            camp.Slug,
            preferredYear: 2027,
            fallbackToLatestSeason: false);

        detail.Should().BeNull();
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
    public async Task GetCampPublicSummariesForYearAsync_AfterUploadImageAsync_RefreshesCachedImage()
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

        var beforeUpload = await _service.GetCampPublicSummariesForYearAsync(2026);

        beforeUpload.Single(summary => summary.Id == camp.Id).ImageUrl.Should().BeNull();

        await using var imageStream = new MemoryStream([1, 2, 3, 4]);
        var uploadResult = await _service.UploadImageAsync(camp.Id, imageStream, "camp.jpg", "image/jpeg", imageStream.Length);

        uploadResult.Succeeded.Should().BeTrue();

        var afterUpload = await _service.GetCampPublicSummariesForYearAsync(2026);

        afterUpload.Single(summary => summary.Id == camp.Id).ImageUrl.Should().NotBeNull();
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
    public async Task AddCampMemberAsLead_creates_active_member_and_audits()
    {
        // Seed a camp + season + a target user
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "test-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var targetUserId = Guid.NewGuid();
        var leadUserId = Guid.NewGuid();
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();

        var memberId = await _service.AddCampMemberAsLeadAsync(season.Id, targetUserId, leadUserId);

        memberId.Should().NotBe(Guid.Empty);
        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == memberId);
        member.UserId.Should().Be(targetUserId);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedByUserId.Should().Be(leadUserId);

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampMemberAddedByLead,
            nameof(CampMember), memberId,
            Arg.Any<string>(), leadUserId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddCampMemberAsLead_returns_existing_id_when_already_active()
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

        var leadId = Guid.NewGuid();
        var memberId = await _service.AddCampMemberAsLeadAsync(season.Id, userId, leadId);

        memberId.Should().Be(existing.Id);
        // No new audit log for an already-active member.
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.CampMemberAddedByLead, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task AddCampMemberToActiveSeasonAsLead_uses_active_season()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "active-camp" };
        var inactiveSeason = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2025, Status = CampSeasonStatus.Pending };
        var activeSeason = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Active };
        var targetUserId = Guid.NewGuid();
        var leadUserId = Guid.NewGuid();
        Db.Camps.Add(camp);
        await Db.CampSeasons.AddRangeAsync(inactiveSeason, activeSeason);
        await Db.SaveChangesAsync();

        var result = await _service.AddCampMemberToActiveSeasonAsLeadAsync(camp.Id, targetUserId, leadUserId);

        result.Outcome.Should().Be(AddCampMemberAsLeadOutcome.Added);
        result.CampMemberId.Should().NotBeNull();
        var member = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == result.CampMemberId);
        member.CampSeasonId.Should().Be(activeSeason.Id);
    }

    [HumansFact]
    public async Task AddCampMemberToActiveSeasonAsLead_without_active_season_returns_no_active_season()
    {
        var camp = new Camp { Id = Guid.NewGuid(), Slug = "inactive-camp" };
        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = 2026, Status = CampSeasonStatus.Pending };
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();

        var result = await _service.AddCampMemberToActiveSeasonAsLeadAsync(
            camp.Id, Guid.NewGuid(), Guid.NewGuid());

        result.Outcome.Should().Be(AddCampMemberAsLeadOutcome.NoActiveSeason);
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
    public async Task GetMembershipStateForCampAsync_ActiveMember_ReturnsActive()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var state = await _service.GetMembershipStateForCampAsync(camp.Id, userId);

        state.Status.Should().Be(CampMemberStatusSummary.Active);
        state.OpenSeasonYear.Should().Be(2026);
    }

    [HumansFact]
    public async Task GetMembershipStateForCampAsync_NoSeason_ReturnsNoOpenSeason()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();

        var state = await _service.GetMembershipStateForCampAsync(camp.Id, Guid.NewGuid());

        state.Status.Should().Be(CampMemberStatusSummary.NoOpenSeason);
    }

    [HumansFact]
    public async Task GetPendingMembershipCountForLeadAsync_CountsPendingOnActiveSeasonsForLeadsCamps()
    {
        await SeedSettingsAsync();
        await SeedSpecialDefinitionAsync(CampSpecialRole.Lead);
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "Lead Larry");
        var camp = await _service.CreateCampAsync(
            leadUserId, "Lead Camp", "lc@camp.com", "+34600000020",
            null, null, false, 1, MakeSeasonData(), null, 2026);
        await ApproveLatestSeasonAsync(camp.Id);

        // Two humans request; count should be 2.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await SeedUserAsync(alice, "Alice");
        await SeedUserAsync(bob, "Bob");
        await _service.RequestCampMembershipAsync(camp.Id, alice);
        await _service.RequestCampMembershipAsync(camp.Id, bob);

        (await _service.GetPendingMembershipCountForLeadAsync(leadUserId)).Should().Be(2);

        // A non-lead sees 0.
        (await _service.GetPendingMembershipCountForLeadAsync(Guid.NewGuid())).Should().Be(0);

        // Approve one; count drops to 1.
        var aliceReq = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.UserId == alice);
        await _service.ApproveCampMemberAsync(camp.Id, aliceReq.Id, leadUserId);
        (await _service.GetPendingMembershipCountForLeadAsync(leadUserId)).Should().Be(1);

        // Withdraw the season; count drops to 0 (pending rows remain, but meter
        // filters to Active/Full seasons only).
        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        await _service.WithdrawSeasonAsync(season.Id);
        (await _service.GetPendingMembershipCountForLeadAsync(leadUserId)).Should().Be(0);
    }

    [HumansFact]
    public async Task GetCampMembersAsync_ReturnsOnlyRealCampMemberRows_AfterCampLeadRetirement()
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
        var season = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);

        var memberUserId = Guid.NewGuid();
        await SeedUserAsync(memberUserId, "Member Mary");
        var req = await _service.RequestCampMembershipAsync(camp.Id, memberUserId);
        await _service.ApproveCampMemberAsync(camp.Id, req.CampMemberId, Guid.NewGuid());

        var members = await _service.GetCampMembersAsync(season.Id);

        // Two real Active members: the creator-lead (real row) + the approved member.
        members.Active.Should().HaveCount(2);
        members.Active.Should().Contain(r => r.UserId == memberUserId && r.CampMemberId == req.CampMemberId);
        var creatorRow = members.Active.Should().ContainSingle(r => r.UserId == leadUserId).Subject;
        creatorRow.CampMemberId.Should().NotBe(Guid.Empty);
    }

    [HumansFact]
    public async Task GetCampMembershipsForUserAsync_ActiveMembership_IsReturned()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var req = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.ApproveCampMemberAsync(camp.Id, req.CampMemberId, Guid.NewGuid());

        var memberships = await _service.GetCampMembershipsForUserAsync(userId);

        memberships.Should().HaveCount(1);
        memberships[0].Status.Should().Be(CampMemberStatus.Active);
        memberships[0].Year.Should().Be(2026);
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

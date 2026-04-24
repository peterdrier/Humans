using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampService _service;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly InMemoryCampImageStorage _imageStorage;
    private readonly INotificationEmitter _notificationEmitter;

    public CampServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();
        _imageStorage = new InMemoryCampImageStorage();

        var factory = new TestDbContextFactory(options);
        var repo = new CampRepository(factory);

        // IUserService substitute — returns seeded users from the shared in-memory db.
        _userService = Substitute.For<IUserService>();
        _userService.GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                using var ctx = new HumansDbContext(options);
                var users = ctx.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToList();
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                    users.ToDictionary(u => u.Id));
            });

        _notificationEmitter = Substitute.For<INotificationEmitter>();

        _service = new CampService(
            repo,
            _userService,
            _auditLog,
            Substitute.For<ISystemTeamSync>(),
            _imageStorage,
            _notificationEmitter,
            Substitute.For<ICampLeadJoinRequestsBadgeCacheInvalidator>(),
            _clock,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CampService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateCampAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateCampAsync_NewCamp_CreatesCampWithPendingSeason()
    {
        await SeedSettingsAsync();
        var userId = Guid.NewGuid();

        var camp = await _service.CreateCampAsync(
            userId, "Camp Funhouse", "camp@fun.com", "+34612345678",
            "https://instagram.com/funhouse", null,
            isSwissCamp: false, timesAtNowhere: 0,
            MakeSeasonData(), historicalNames: null, year: 2026);

        camp.Slug.Should().Be("camp-funhouse");
        camp.CreatedByUserId.Should().Be(userId);

        var season = await _dbContext.CampSeasons
            .FirstOrDefaultAsync(s => s.CampId == camp.Id);
        season.Should().NotBeNull();
        season!.Status.Should().Be(CampSeasonStatus.Pending);
        season.Year.Should().Be(2026);
        season.Name.Should().Be("Camp Funhouse");

        var lead = await _dbContext.CampLeads
            .FirstOrDefaultAsync(l => l.CampId == camp.Id);
        lead.Should().NotBeNull();
        lead!.UserId.Should().Be(userId);
        lead.Role.Should().Be(CampLeadRole.CoLead);
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
    // ApproveSeasonAsync
    // ==========================================================================

    [HumansFact]
    public async Task ApproveSeasonAsync_PendingSeason_SetsActive()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        var adminId = Guid.NewGuid();

        await _service.ApproveSeasonAsync(season.Id, adminId, "Looks good");

        var updated = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
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
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);

        await _service.RejectSeasonAsync(season.Id, Guid.NewGuid(), "Not a real camp");

        var updated = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
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
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        // Open 2027 season in settings
        var settings = await _dbContext.CampSettings.FirstAsync();
        settings.OpenSeasons = new List<int> { 2026, 2027 };
        await _dbContext.SaveChangesAsync();

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
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.RejectSeasonAsync(season.Id, Guid.NewGuid(), "nope");

        var settings = await _dbContext.CampSettings.FirstAsync();
        settings.OpenSeasons = new List<int> { 2026, 2027 };
        await _dbContext.SaveChangesAsync();

        var newSeason = await _service.OptInToSeasonAsync(camp.Id, 2027);

        newSeason.Status.Should().Be(CampSeasonStatus.Pending);
    }

    [HumansFact]
    public async Task OptInToSeasonAsync_PendingOnly_GoesPending()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        // Don't approve or reject — season stays Pending

        var settings = await _dbContext.CampSettings.FirstAsync();
        settings.OpenSeasons = new List<int> { 2026, 2027 };
        await _dbContext.SaveChangesAsync();

        var newSeason = await _service.OptInToSeasonAsync(camp.Id, 2027);

        newSeason.Status.Should().Be(CampSeasonStatus.Pending);
    }

    // ==========================================================================
    // AddLeadAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddLeadAsync_UnderMax_AddsLead()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var newLeadId = Guid.NewGuid();

        var lead = await _service.AddLeadAsync(camp.Id, newLeadId);

        lead.UserId.Should().Be(newLeadId);
    }

    [HumansFact]
    public async Task AddLeadAsync_AtMaxLeads_Throws()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        // Add 4 more leads (1 creator + 4 = 5 max)
        for (var i = 0; i < 4; i++)
            await _service.AddLeadAsync(camp.Id, Guid.NewGuid());

        var act = () => _service.AddLeadAsync(camp.Id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum*");
    }

    [HumansFact]
    public async Task RemoveLeadAsync_LastLead_Throws()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var lead = await _dbContext.CampLeads.FirstAsync(l => l.CampId == camp.Id);

        var act = () => _service.RemoveLeadAsync(lead.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last lead*");
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

        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = zebraCamp.Id,
            StoragePath = "uploads/camps/zebra.jpg",
            SortOrder = 1,
            UploadedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

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
                ContainerCount = 2,
                ContainerNotes = "Two containers",
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
                ContainerCount = 0,
                ContainerNotes = null,
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
        placements[1].ContainerCount.Should().Be(2);
        placements[1].ContainerNotes.Should().Be("Two containers");
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

        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        season.NameLockDate = new LocalDate(2026, 3, 1);

        _dbContext.CampImages.Add(new CampImage
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            StoragePath = "uploads/camps/fallback.jpg",
            SortOrder = 1,
            UploadedAt = _clock.GetCurrentInstant()
        });

        var settings = await _dbContext.CampSettings.FirstAsync();
        settings.PublicYear = 2027;
        await _dbContext.SaveChangesAsync();

        var detail = await _service.GetCampDetailAsync(camp.Slug);

        detail.Should().NotBeNull();
        detail!.Name.Should().Be("Fallback Camp");
        detail.Links.Should().ContainSingle(link => link.Url == "https://example.com/fallback");
        detail.HistoricalNames.Should().Contain("Old Fallback");
        detail.ImageUrls.Should().ContainSingle("/uploads/camps/fallback.jpg");
        detail.Leads.Should().ContainSingle(lead => lead.DisplayName == "Camp Lead");
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

        var detail = await _service.GetCampDetailAsync(
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
        await _service.UploadImageAsync(camp.Id, imageStream, "camp.jpg", "image/jpeg", imageStream.Length);

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
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        await _service.ChangeSeasonNameAsync(season.Id, "New Name");

        var updated = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        updated.Name.Should().Be("New Name");

        var historical = await _dbContext.CampHistoricalNames
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.CampId == camp.Id && h.Source == CampNameSource.NameChange);
        historical.Should().NotBeNull();
        historical!.Name.Should().Be("Test Camp");
    }

    [HumansFact]
    public async Task ChangeSeasonNameAsync_AfterLockDate_Throws()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        // Set lock date in the past
        season.NameLockDate = new LocalDate(2026, 3, 1);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.ChangeSeasonNameAsync(season.Id, "Too Late");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*locked*");
    }

    // ==========================================================================
    // Camp membership per season (issue nobodies-collective#488)
    // ==========================================================================

    [Fact]
    public async Task RequestCampMembershipAsync_OpenSeason_CreatesPending()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");

        var result = await _service.RequestCampMembershipAsync(camp.Id, userId);

        result.Outcome.Should().Be(CampMemberRequestOutcome.Created);
        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == result.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Pending);
        member.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task RequestCampMembershipAsync_NoOpenSeason_ReturnsNoOpenSeason()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");

        var result = await _service.RequestCampMembershipAsync(camp.Id, userId);

        result.Outcome.Should().Be(CampMemberRequestOutcome.NoOpenSeason);
        (await _dbContext.CampMembers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
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
        second.CampMemberId.Should().Be(first.CampMemberId);
        (await _dbContext.CampMembers.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
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

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Active);
        member.ConfirmedByUserId.Should().Be(approverId);
        member.ConfirmedAt.Should().NotBeNull();

        await _notificationEmitter.Received(1).SendAsync(
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

    [Fact]
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
        var memberB = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == requestInCampB.CampMemberId);
        memberB.Status.Should().Be(CampMemberStatus.Pending);
    }

    [Fact]
    public async Task RejectCampMemberAsync_PendingRequest_SetsRemovedAndNotifies()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);

        await _service.RejectCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
        member.RemovedAt.Should().NotBeNull();

        await _notificationEmitter.Received(1).SendAsync(
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

    [Fact]
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

    [Fact]
    public async Task WithdrawCampMembershipRequestAsync_Self_SetsRemoved()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);

        await _service.WithdrawCampMembershipRequestAsync(request.CampMemberId, userId);

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [Fact]
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

    [Fact]
    public async Task LeaveCampAsync_ActiveMember_SetsRemoved()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        await _service.ApproveCampMemberAsync(camp.Id, request.CampMemberId, Guid.NewGuid());

        await _service.LeaveCampAsync(request.CampMemberId, userId);

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [Fact]
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

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Removed);
    }

    [Fact]
    public async Task WithdrawSeasonAsync_NotifiesPendingRequesters_DoesNotChangeMemberStatus()
    {
        // No more auto-withdraw cascade — just a notification out. Pending rows stay pending.
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        await ApproveLatestSeasonAsync(camp.Id);
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId, "Alice");
        var request = await _service.RequestCampMembershipAsync(camp.Id, userId);
        var season = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        _notificationEmitter.ClearReceivedCalls();

        await _service.WithdrawSeasonAsync(season.Id);

        var member = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.Id == request.CampMemberId);
        member.Status.Should().Be(CampMemberStatus.Pending);

        await _notificationEmitter.Received(1).SendAsync(
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

    [Fact]
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

    [Fact]
    public async Task GetMembershipStateForCampAsync_NoSeason_ReturnsNoOpenSeason()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();

        var state = await _service.GetMembershipStateForCampAsync(camp.Id, Guid.NewGuid());

        state.Status.Should().Be(CampMemberStatusSummary.NoOpenSeason);
    }

    [Fact]
    public async Task GetPendingMembershipCountForLeadAsync_CountsPendingOnActiveSeasonsForLeadsCamps()
    {
        await SeedSettingsAsync();
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
        var aliceReq = await _dbContext.CampMembers.AsNoTracking().FirstAsync(m => m.UserId == alice);
        await _service.ApproveCampMemberAsync(camp.Id, aliceReq.Id, leadUserId);
        (await _service.GetPendingMembershipCountForLeadAsync(leadUserId)).Should().Be(1);

        // Withdraw the season; count drops to 0 (pending rows remain, but meter
        // filters to Active/Full seasons only).
        var season = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);
        await _service.WithdrawSeasonAsync(season.Id);
        (await _service.GetPendingMembershipCountForLeadAsync(leadUserId)).Should().Be(0);
    }

    [Fact]
    public async Task GetCampMembersAsync_IncludesLeadsAsActiveWithLeadBadge()
    {
        await SeedSettingsAsync();
        var leadUserId = Guid.NewGuid();
        await SeedUserAsync(leadUserId, "Lead Larry");
        // Use the lead as the creator so they're registered as a CampLead.
        var camp = await _service.CreateCampAsync(
            leadUserId, "Lead Camp", "lc@camp.com", "+34600000010",
            null, null, false, 1, MakeSeasonData(), null, 2026);
        await ApproveLatestSeasonAsync(camp.Id);
        var season = await _dbContext.CampSeasons.AsNoTracking().FirstAsync(s => s.CampId == camp.Id);

        // A separate human requests and is approved.
        var memberUserId = Guid.NewGuid();
        await SeedUserAsync(memberUserId, "Member Mary");
        var req = await _service.RequestCampMembershipAsync(camp.Id, memberUserId);
        await _service.ApproveCampMemberAsync(camp.Id, req.CampMemberId, Guid.NewGuid());

        var members = await _service.GetCampMembersAsync(season.Id);

        // Lead appears without a CampMember row but with IsLead=true.
        var leadRow = members.Active.Single(r => r.UserId == leadUserId);
        leadRow.IsLead.Should().BeTrue();
        leadRow.CampMemberId.Should().Be(Guid.Empty);

        // Approved member appears normally.
        var memberRow = members.Active.Single(r => r.UserId == memberUserId);
        memberRow.IsLead.Should().BeFalse();
        memberRow.CampMemberId.Should().Be(req.CampMemberId);

        // Leads sort to the top.
        members.Active[0].IsLead.Should().BeTrue();
    }

    [Fact]
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
        Vibes: new List<CampVibe> { CampVibe.LiveMusic, CampVibe.ChillOut },
        AdultPlayspace: AdultPlayspacePolicy.No,
        MemberCount: 25,
        SpaceRequirement: SpaceSize.Sqm600,
        SoundZone: SoundZone.Yellow,
        ContainerCount: 1,
        ContainerNotes: null,
        ElectricalGrid: ElectricalGrid.Yellow);

    private async Task<Camp> CreateTestCamp()
    {
        return await _service.CreateCampAsync(
            Guid.NewGuid(), "Test Camp", "test@camp.com", "+34600000000",
            null, null, false, 1, MakeSeasonData(), null, 2026);
    }

    private async Task ApproveLatestSeasonAsync(Guid campId)
    {
        var season = await _dbContext.CampSeasons
            .Where(s => s.CampId == campId)
            .OrderByDescending(s => s.Year)
            .FirstAsync();

        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);
    }

    private async Task SeedSettingsAsync()
    {
        if (!await _dbContext.CampSettings.AnyAsync())
        {
            _dbContext.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = 2026,
                OpenSeasons = new List<int> { 2026 }
            });
            await _dbContext.SaveChangesAsync();
        }
    }

    private async Task SeedUserAsync(Guid userId, string displayName)
    {
        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = $"{displayName.Replace(" ", string.Empty, StringComparison.Ordinal)}@example.com",
            Email = $"{displayName.Replace(" ", string.Empty, StringComparison.Ordinal)}@example.com",
            DisplayName = displayName
        });

        await _dbContext.SaveChangesAsync();
    }
}

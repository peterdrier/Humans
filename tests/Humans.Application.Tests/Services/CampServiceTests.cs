using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
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

    public CampServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();

        _service = new CampService(
            _dbContext,
            _auditLog,
            Substitute.For<ISystemTeamSync>(),
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task ApproveSeasonAsync_PendingSeason_SetsActive()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        var adminId = Guid.NewGuid();

        await _service.ApproveSeasonAsync(season.Id, adminId, "Looks good");

        var updated = await _dbContext.CampSeasons.FindAsync(season.Id);
        updated!.Status.Should().Be(CampSeasonStatus.Active);
        updated.ReviewedByUserId.Should().Be(adminId);
        updated.ReviewNotes.Should().Be("Looks good");
        updated.ResolvedAt.Should().NotBeNull();
    }

    // ==========================================================================
    // RejectSeasonAsync
    // ==========================================================================

    [Fact]
    public async Task RejectSeasonAsync_PendingSeason_SetsRejected()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);

        await _service.RejectSeasonAsync(season.Id, Guid.NewGuid(), "Not a real camp");

        var updated = await _dbContext.CampSeasons.FindAsync(season.Id);
        updated!.Status.Should().Be(CampSeasonStatus.Rejected);
        updated.ReviewNotes.Should().Be("Not a real camp");
    }

    // ==========================================================================
    // OptInToSeasonAsync
    // ==========================================================================

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task AddLeadAsync_UnderMax_AddsLead()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var newLeadId = Guid.NewGuid();

        var lead = await _service.AddLeadAsync(camp.Id, newLeadId);

        lead.UserId.Should().Be(newLeadId);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task IsUserCampLeadAsync_ActiveLead_ReturnsTrue()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var leadUserId = camp.CreatedByUserId;

        var result = await _service.IsUserCampLeadAsync(leadUserId, camp.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsUserCampLeadAsync_NonLead_ReturnsFalse()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var result = await _service.IsUserCampLeadAsync(Guid.NewGuid(), camp.Id);
        result.Should().BeFalse();
    }

    // ==========================================================================
    // ChangeSeasonNameAsync
    // ==========================================================================

    [Fact]
    public async Task ChangeSeasonNameAsync_LogsOldNameToHistory()
    {
        await SeedSettingsAsync();
        var camp = await CreateTestCamp();
        var season = await _dbContext.CampSeasons.FirstAsync(s => s.CampId == camp.Id);
        await _service.ApproveSeasonAsync(season.Id, Guid.NewGuid(), null);

        await _service.ChangeSeasonNameAsync(season.Id, "New Name");

        var updated = await _dbContext.CampSeasons.FindAsync(season.Id);
        updated!.Name.Should().Be("New Name");

        var historical = await _dbContext.CampHistoricalNames
            .FirstOrDefaultAsync(h => h.CampId == camp.Id && h.Source == CampNameSource.NameChange);
        historical.Should().NotBeNull();
        historical!.Name.Should().Be("Test Camp");
    }

    [Fact]
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
}

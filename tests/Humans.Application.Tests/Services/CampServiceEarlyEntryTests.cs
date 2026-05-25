using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class CampServiceEarlyEntryTests : ServiceTestHarness
{
    private readonly CampService _service;
    private readonly IUserService _userService;
    private readonly InMemoryFileStorage _fileStorage;
    private readonly ICampRoleService _campRoleService;

    public CampServiceEarlyEntryTests()
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
    // SetEeStartDateAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetEeStartDateAsync_SetsValue_AndInvalidatesSettingsCache()
    {
        await SeedSettingsAsync();
        var date = new LocalDate(2026, 8, 7);
        var actorUserId = Guid.NewGuid();

        await _service.SetEeStartDateAsync(date, actorUserId);

        var settings = await _service.GetSettingsAsync();
        settings.EeStartDate.Should().Be(date);

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampSettingsEeStartDateChanged,
            nameof(CampSettings), Arg.Any<Guid>(),
            Arg.Any<string>(), actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // SetCampSeasonEeSlotCountAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetCampSeasonEeSlotCountAsync_SetsValue_AndAuditsChange()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync();
        var actor = Guid.NewGuid();

        await _service.SetCampSeasonEeSlotCountAsync(season.Id, 13, actor);

        var reloaded = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        reloaded.EeSlotCount.Should().Be(13);

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampSeasonEeSlotCountChanged,
            nameof(CampSeason), season.Id,
            Arg.Any<string>(), actor,
            camp.Id, nameof(Camp));
    }

    [HumansFact]
    public async Task SetCampSeasonEeSlotCountAsync_AllowsReducingBelowCurrentGrants()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 10);
        // Seed 5 active members all with HasEarlyEntry=true.
        for (var i = 0; i < 5; i++)
            await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var actor = Guid.NewGuid();
        await _service.SetCampSeasonEeSlotCountAsync(season.Id, 3, actor);

        var reloaded = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id);
        reloaded.EeSlotCount.Should().Be(3);

        // Existing grants persist — no auto-revoke.
        var grantedCount = await Db.CampMembers
            .CountAsync(m => m.CampSeasonId == season.Id
                          && m.HasEarlyEntry
                          && m.Status == CampMemberStatus.Active);
        grantedCount.Should().Be(5);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

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

    private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(int initialEeSlotCount = 0)
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
            Year = 2026,
            Status = CampSeasonStatus.Active,
            Name = "Test Camp",
            EeSlotCount = initialEeSlotCount,
            BlurbLong = "A fun camp for everyone",
            BlurbShort = "Fun camp",
            Languages = "English, Spanish",
            AcceptingMembers = YesNoMaybe.Yes,
            KidsWelcome = YesNoMaybe.Maybe,
            KidsVisiting = KidsVisitingPolicy.DaytimeOnly,
            HasPerformanceSpace = PerformanceSpaceStatus.Yes,
            PerformanceTypes = "Music, dance",
            Vibes = [CampVibe.LiveMusic, CampVibe.ChillOut],
            AdultPlayspace = AdultPlayspacePolicy.No,
            MemberCount = 25,
            SpaceRequirement = SpaceSize.Sqm600,
            SoundZone = SoundZone.Yellow,
            ElectricalGrid = ElectricalGrid.Yellow,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.Camps.Add(camp);
        Db.CampSeasons.Add(season);
        await Db.SaveChangesAsync();
        return (camp, season);
    }

    private async Task<CampMember> SeedActiveMemberWithEarlyEntryAsync(Guid campSeasonId)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
            HasEarlyEntry = true,
        };
        Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();
        return member;
    }

    private async Task<CampMember> SeedActiveMemberAsync(Guid campSeasonId)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = Clock.GetCurrentInstant(),
            ConfirmedAt = Clock.GetCurrentInstant(),
            HasEarlyEntry = false,
        };
        Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();
        return member;
    }

    // ==========================================================================
    // SetEarlyEntryAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetEarlyEntryAsync_Grant_SetsFlagAndAudits()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberAsync(season.Id);
        var actor = Guid.NewGuid();

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, actor);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);
        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.HasEarlyEntry.Should().BeTrue();

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampEarlyEntryGranted,
            nameof(CampMember), member.Id,
            Arg.Any<string>(), actor,
            camp.Id, nameof(Camp));
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Revoke_ClearsFlagAndAudits()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        var actor = Guid.NewGuid();

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: false, actor);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);
        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();

        await AuditLog.Received(1).LogAsync(
            AuditAction.CampEarlyEntryRevoked,
            nameof(CampMember), member.Id,
            Arg.Any<string>(), actor,
            camp.Id, nameof(Camp));
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Grant_ReturnsSlotCapExceeded_WhenCapWouldBeBreached()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 2);
        await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        var newMember = await SeedActiveMemberAsync(season.Id);

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, newMember.Id, granted: true, Guid.NewGuid());

        outcome.Should().Be(SetEarlyEntryOutcome.SlotCapExceeded);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == newMember.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.CampEarlyEntryGranted,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Grant_ReturnsMemberNotActive_WhenMemberIsPending()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = season.Id,
            UserId = Guid.NewGuid(),
            Status = CampMemberStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant(),
        };
        Db.CampMembers.Add(member);
        await Db.SaveChangesAsync();

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, Guid.NewGuid());

        outcome.Should().Be(SetEarlyEntryOutcome.MemberNotActive);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Idempotent_ReturnsNoChangeAndWritesNoAudit()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, Guid.NewGuid());

        outcome.Should().Be(SetEarlyEntryOutcome.NoChange);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.CampEarlyEntryGranted,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_ReturnsMemberNotFound_WhenMemberBelongsToDifferentCamp()
    {
        await SeedSettingsAsync();
        var (campA, _) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var (_, seasonB) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var memberInB = await SeedActiveMemberAsync(seasonB.Id);

        // Attacker scopes the call to campA but targets campB's member.
        var outcome = await _service.SetEarlyEntryAsync(
            campA.Id, memberInB.Id, granted: true, Guid.NewGuid());

        outcome.Should().Be(SetEarlyEntryOutcome.MemberNotFound);

        var reloaded = await Db.CampMembers.AsNoTracking()
            .FirstAsync(m => m.Id == memberInB.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }

    // ==========================================================================
    // Removal-path HasEarlyEntry cascade (issue nobodies-collective#490)
    // ==========================================================================

    [HumansFact]
    public async Task RemoveCampMemberAsync_ClearsHasEarlyEntry()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        await _service.RemoveCampMemberAsync(camp.Id, member.Id, Guid.NewGuid());

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();
        reloaded.Status.Should().Be(CampMemberStatus.Removed);
    }

    [HumansFact]
    public async Task LeaveCampAsync_ClearsHasEarlyEntry()
    {
        await SeedSettingsAsync();
        var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var result = await _service.LeaveCampAsync(member.Id, member.UserId);

        result.Succeeded.Should().BeTrue();

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }
}

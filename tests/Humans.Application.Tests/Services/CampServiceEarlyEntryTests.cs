using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.EarlyEntry;
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
    private readonly InMemoryFileStorage _fileStorage;
    private readonly ICampRoleService _campRoleService;
    private readonly IUserServiceRead _userServiceRead;

    public CampServiceEarlyEntryTests()
        : base(Instant.FromUtc(2026, 3, 13, 12, 0))
    {
        _fileStorage = new InMemoryFileStorage();

        var repo = new CampRepository(DbFactory);

        _campRoleService = Substitute.For<ICampRoleService>();
        _campRoleService.RemoveAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _userServiceRead = Substitute.For<IUserServiceRead>();

        _service = new CampService(
            repo,
            AuditLog,
            Substitute.For<ISystemTeamSync>(),
            _fileStorage,
            Notifier,
            Substitute.For<ICampLeadJoinRequestsBadgeCacheInvalidator>(),
            new Lazy<ICampRoleService>(() => _campRoleService),
            Substitute.For<IEarlyEntryInvalidator>(),
            _userServiceRead,
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

        await _service.SetEeStartDateAsync(date, actorUserId, Xunit.TestContext.Current.CancellationToken);

        var settings = await _service.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken);
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

        await _service.SetCampSeasonEeSlotCountAsync(season.Id, 13, actor, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id, Xunit.TestContext.Current.CancellationToken);
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
        var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 10);
        // Seed 5 active members all with HasEarlyEntry=true.
        for (var i = 0; i < 5; i++)
            await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var actor = Guid.NewGuid();
        await _service.SetCampSeasonEeSlotCountAsync(season.Id, 3, actor, Xunit.TestContext.Current.CancellationToken);

        var reloaded = await Db.CampSeasons.AsNoTracking().FirstAsync(s => s.Id == season.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.EeSlotCount.Should().Be(3);

        // Existing grants persist — no auto-revoke.
        var grantedCount = await Db.CampMembers
            .CountAsync(m => m.CampSeasonId == season.Id
                          && m.HasEarlyEntry
                          && m.Status == CampMemberStatus.Active, Xunit.TestContext.Current.CancellationToken);
        grantedCount.Should().Be(5);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task SeedSettingsAsync()
    {
        if (!await Db.CampSettings.AnyAsync(Xunit.TestContext.Current.CancellationToken))
        {
            Db.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = 2026,
                OpenSeasons = [2026]
            });
            await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, actor, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);
        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
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

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: false, actor, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);
        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
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

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, newMember.Id, granted: true, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.SlotCapExceeded);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == newMember.Id, Xunit.TestContext.Current.CancellationToken);
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
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.MemberNotActive);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Idempotent_ReturnsNoChangeAndWritesNoAudit()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var outcome = await _service.SetEarlyEntryAsync(camp.Id, member.Id, granted: true, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

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
            campA.Id, memberInB.Id, granted: true, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.MemberNotFound);

        var reloaded = await Db.CampMembers.AsNoTracking()
            .FirstAsync(m => m.Id == memberInB.Id, Xunit.TestContext.Current.CancellationToken);
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

        await _service.RemoveCampMemberAsync(camp.Id, member.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeFalse();
        reloaded.Status.Should().Be(CampMemberStatus.Removed);
    }

    [HumansFact]
    public async Task LeaveCampAsync_ClearsHasEarlyEntry()
    {
        await SeedSettingsAsync();
        var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

        var result = await _service.LeaveCampAsync(member.Id, member.UserId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }

    // ==========================================================================
    // Entered-event guard — a grant is consumed once its holder is through the
    // gate; it can no longer be revoked or freed by removal/leave.
    // ==========================================================================

    private void SetUserParticipation(Guid userId, int year, ParticipationStatus status)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = "Test",
            PreferredLanguage = "en",
            CreatedAt = Clock.GetCurrentInstant(),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        var participation = new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = status,
            Source = ParticipationSource.TicketSync,
        };
        _userServiceRead.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                user, [], [participation], [], null, [], [], [], [])));
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Revoke_ReturnsMemberAlreadyEntered_WhenHolderAttendedSeasonYear()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(member.UserId, season.Year, ParticipationStatus.Attended);

        var outcome = await _service.SetEarlyEntryAsync(
            camp.Id, member.Id, granted: false, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.MemberAlreadyEntered);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeTrue();

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.CampEarlyEntryRevoked,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Revoke_Succeeds_WhenHolderOnlyTicketed()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(member.UserId, season.Year, ParticipationStatus.Ticketed);

        var outcome = await _service.SetEarlyEntryAsync(
            camp.Id, member.Id, granted: false, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Revoke_Succeeds_WhenHolderAttendedDifferentYear()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(member.UserId, season.Year - 1, ParticipationStatus.Attended);

        var outcome = await _service.SetEarlyEntryAsync(
            camp.Id, member.Id, granted: false, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.Success);
    }

    [HumansFact]
    public async Task RemoveCampMemberAsync_RetainsHasEarlyEntry_WhenHolderAttended()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(member.UserId, season.Year, ParticipationStatus.Attended);

        await _service.RemoveCampMemberAsync(camp.Id, member.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(CampMemberStatus.Removed);
        reloaded.HasEarlyEntry.Should().BeTrue("the consumed slot must keep counting against the cap");
    }

    [HumansFact]
    public async Task LeaveCampAsync_RetainsHasEarlyEntry_WhenHolderAttended()
    {
        await SeedSettingsAsync();
        var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
        var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(member.UserId, season.Year, ParticipationStatus.Attended);

        var result = await _service.LeaveCampAsync(member.Id, member.UserId, Xunit.TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == member.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetEarlyEntryAsync_Grant_CountsConsumedSlots_FromRemovedAttendedMembers()
    {
        await SeedSettingsAsync();
        var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 1);
        var entered = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
        SetUserParticipation(entered.UserId, season.Year, ParticipationStatus.Attended);

        // The cheat: remove the member who already entered, then try to hand
        // the freed slot to someone else.
        await _service.RemoveCampMemberAsync(camp.Id, entered.Id, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        var second = await SeedActiveMemberAsync(season.Id);

        var outcome = await _service.SetEarlyEntryAsync(
            camp.Id, second.Id, granted: true, Guid.NewGuid(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        outcome.Should().Be(SetEarlyEntryOutcome.SlotCapExceeded);

        var reloaded = await Db.CampMembers.AsNoTracking().FirstAsync(m => m.Id == second.Id, Xunit.TestContext.Current.CancellationToken);
        reloaded.HasEarlyEntry.Should().BeFalse();
    }
}

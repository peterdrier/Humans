using Humans.Shifts.Domain;
using Humans.Auth.Contracts;
using Humans.Teams.Domain;
using Humans.EarlyEntry.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Base.Enums;
using Humans.Shifts.Data;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Active accounts' Public-rota signups auto-confirm regardless of their
/// admission/consent status; only RequireApproval rotas park signups as Pending.
/// </summary>
public sealed class ShiftSignupServiceAutoConfirmIgnoresConsentTests : ShiftsTestHarness
{
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftRepository _repo;
    private readonly ShiftSignupService _service;
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly UserInfo _userInfo;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly Guid _userId = Guid.NewGuid();

    public ShiftSignupServiceAutoConfirmIgnoresConsentTests()
        : base(TestNow)
    {
        _userInfo = UserInfoStubHelpers.MakeUserInfo(_userId) with
        {
            State = UserState.Active,
            Profile = UserFixtures.Profile(
                burnerName: "New Human", firstName: "New", lastName: "Human", isApproved: false),
        };
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(_userInfo);
        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = new ServiceLocatorBuilder()
            .With(teamService)
            .With<ITeamServiceRead>(teamService)
            .With(roleAssignmentService)
            .With(_users)
            .Build();

        var shiftRepo = new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock);

        _shiftMgmt = new ShiftManagementService(
            shiftRepo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            NewCalendarResolver(),
            Clock);

        _repo = new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock);
        _service = new ShiftSignupService(
            _repo,
            Substitute.For<IVolunteerTrackingRepository>(),
            _shiftMgmt,
            NewCalendarResolver(),
            AuditLog,
            Substitute.For<INotificationEmitter>(),
            AdminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IEarlyEntryInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance,
            _users,
            Substitute.For<IStringLocalizer<ShiftsResource>>());
    }

    [HumansFact]
    public async Task SignUp_PublicRota_UserMissingConsents_ReturnsConfirmed()
    {
        Assert.True(_userInfo.HasRequiredNameFields);
        Assert.False(_userInfo.IsApproved);
        var (_, shift) = SeedShiftScenario(SignupPolicy.Public);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Confirmed, Saved(result).Status);
    }

    [HumansFact]
    public async Task SignUp_PublicRota_ApprovedUser_ReturnsConfirmed()
    {
        _users.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(_userInfo with
            {
                Profile = _userInfo.Profile! with { IsApproved = true, ConsentCheckStatus = ConsentCheckStatus.Cleared },
            });
        var (_, shift) = SeedShiftScenario(SignupPolicy.Public);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Confirmed, Saved(result).Status);
    }

    [HumansFact]
    public async Task SignUp_RequireApprovalRota_UserMissingConsents_StaysPending()
    {
        var (_, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Pending, Saved(result).Status);
    }

    [HumansFact]
    public async Task SignUpRange_PublicBuildRota_UserMissingConsents_AllBlockShiftsConfirmed()
    {
        var (rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
        {
            SeedAllDayShift(rota, day);
        }
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(_userId, rota.Id, -3, -1, _userId);

        Assert.True(result.Success);
        var blockSignups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == _userId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, blockSignups.Count);
        Assert.All(blockSignups, s => Assert.Equal(SignupStatus.Confirmed, s.Status));
        Assert.NotNull(blockSignups[0].SignupBlockId);
        Assert.True(blockSignups.All(s => s.SignupBlockId == blockSignups[0].SignupBlockId));
    }

    private (Rota rota, Shift shift) SeedShiftScenario(SignupPolicy policy)
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event 2026",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        ShiftsDb.EventSettings.Add(es);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Department",
            Slug = "test-dept",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        TeamsDb.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = policy,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        ShiftsDb.Rotas.Add(rota);

        var shift = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        return (rota, shift);
    }

    private Shift SeedShift(Rota rota, int dayOffset, int startHour, double durationHours)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(startHour, 0),
            Duration = Duration.FromHours(durationHours),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };
        ShiftsDb.Shifts.Add(shift);
        return shift;
    }

    private void SeedAllDayShift(Rota rota, int dayOffset)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = true,
            StartTime = LocalTime.Midnight,
            Duration = Duration.FromHours(24),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };
        ShiftsDb.Shifts.Add(shift);
    }

    /// <summary>
    /// The persisted signup row a <see cref="SignupResult"/> refers to.
    /// The result carries the id rather than the entity — the row is the
    /// section's own and does not cross the boundary
    /// (nobodies-collective/Humans#866).
    /// </summary>
    private ShiftSignup Saved(SignupResult result) =>
        ShiftsDb.ShiftSignups.Single(s => s.Id == result.SignupId!.Value);

}

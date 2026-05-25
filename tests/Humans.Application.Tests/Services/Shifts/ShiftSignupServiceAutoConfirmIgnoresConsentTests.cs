using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Public-rota signups auto-confirm at creation regardless of the volunteer's
/// admission/consent status; only RequireApproval rotas park signups as Pending.
/// </summary>
public sealed class ShiftSignupServiceAutoConfirmIgnoresConsentTests : ServiceTestHarness
{
    private readonly IMembershipCalculator _membership;
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftSignupRepository _repo;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly Guid _userId = Guid.NewGuid();

    public ShiftSignupServiceAutoConfirmIgnoresConsentTests()
        : base(TestNow)
    {
        _membership = Substitute.For<IMembershipCalculator>();

        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = new ServiceLocatorBuilder()
            .With(teamService)
            .With<ITeamServiceRead>(teamService)
            .With(roleAssignmentService)
            .Build();

        var shiftRepo = new ShiftManagementRepository(DbFactory);

        _shiftMgmt = new ShiftManagementService(
            shiftRepo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        _repo = new ShiftSignupRepository(Db, Clock);
        _service = new ShiftSignupService(
            _repo,
            _shiftMgmt,
            _membership,
            AuditLog,
            Substitute.For<INotificationService>(),
            AdminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IEarlyEntryInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    [HumansFact]
    public async Task SignUp_PublicRota_UserMissingConsents_ReturnsConfirmed()
    {
        SetUserConsents(false);
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        await Db.SaveChangesAsync();

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Confirmed, result.Signup!.Status);
    }

    [HumansFact]
    public async Task SignUp_PublicRota_UserWithConsents_ReturnsConfirmed()
    {
        SetUserConsents(true);
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        await Db.SaveChangesAsync();

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Confirmed, result.Signup!.Status);
    }

    [HumansFact]
    public async Task SignUp_RequireApprovalRota_UserMissingConsents_StaysPending()
    {
        SetUserConsents(false);
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        await Db.SaveChangesAsync();

        var result = await _service.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Pending, result.Signup!.Status);
    }

    [HumansFact]
    public async Task SignUpRange_PublicBuildRota_UserMissingConsents_AllBlockShiftsConfirmed()
    {
        SetUserConsents(false);
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
        {
            SeedAllDayShift(rota, day);
        }
        await Db.SaveChangesAsync();

        var result = await _service.SignUpRangeAsync(_userId, rota.Id, -3, -1, _userId);

        Assert.True(result.Success);
        var blockSignups = await Db.ShiftSignups
            .Where(s => s.UserId == _userId)
            .ToListAsync();
        Assert.Equal(3, blockSignups.Count);
        Assert.All(blockSignups, s => Assert.Equal(SignupStatus.Confirmed, s.Status));
        Assert.NotNull(blockSignups[0].SignupBlockId);
        Assert.True(blockSignups.All(s => s.SignupBlockId == blockSignups[0].SignupBlockId));
    }

    private void SetUserConsents(bool hasConsents)
    {
        _membership.HasAllRequiredConsentsForTeamAsync(
            Arg.Any<Guid>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(hasConsents);
    }

    private (EventSettings es, Rota rota, Shift shift) SeedShiftScenario(SignupPolicy policy)
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
        Db.EventSettings.Add(es);

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
        Db.Teams.Add(team);

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
        Db.Rotas.Add(rota);

        var shift = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        return (es, rota, shift);
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
        Db.Shifts.Add(shift);
        return shift;
    }

    private Shift SeedAllDayShift(Rota rota, int dayOffset)
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
        Db.Shifts.Add(shift);
        return shift;
    }
}

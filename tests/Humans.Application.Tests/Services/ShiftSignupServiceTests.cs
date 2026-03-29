using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ShiftSignupServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLog;
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftSignupService _service;

    // Fixed test time: 2026-06-15 12:00 UTC
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(TestNow);
        _auditLog = Substitute.For<IAuditLogService>();

        _shiftMgmt = new ShiftManagementService(
            _dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            _clock,
            NullLogger<ShiftManagementService>.Instance);

        _service = new ShiftSignupService(
            _dbContext,
            _shiftMgmt,
            _auditLog,
            _clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ============================================================
    // SignUp
    // ============================================================

    [Fact]
    public async Task SignUp_PublicPolicy_CreatesConfirmed()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.Confirmed);
        result.Signup.ReviewedByUserId.Should().Be(userId);
    }

    [Fact]
    public async Task SignUp_RequireApprovalPolicy_CreatesPending()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var userId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.Pending);
        result.Signup.ReviewedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task SignUp_DuplicateSignup_ReturnsError()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Already signed up");
    }

    [Fact]
    public async Task SignUp_OverlappingShift_ReturnsError()
    {
        var (es, rota, shift1) = SeedShiftScenario(SignupPolicy.Public);
        // Create a second shift at overlapping time (same day, same start)
        var shift2 = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        var userId = Guid.NewGuid();
        SeedSignup(userId, shift1.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        // shift1: day 1, 10:00-14:00 — shift2: day 1, 10:00-14:00 (identical times)
        var result = await _service.SignUpAsync(userId, shift2.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Time conflict");
    }

    [Fact]
    public async Task SignUp_SystemClosed_RegularVolunteer_ReturnsError()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        es.IsShiftBrowsingOpen = false;
        var userId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not currently open");
    }

    [Fact]
    public async Task SignUp_AdminOnlyShift_RegularVolunteer_ReturnsError()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.AdminOnly = true;
        var userId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("restricted to coordinators");
    }

    // ============================================================
    // Approve
    // ============================================================

    [Fact]
    public async Task Approve_FromPending_SetsConfirmed()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Pending);
        var reviewerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveAsync(signup.Id, reviewerId);

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.Confirmed);
        result.Signup.ReviewedByUserId.Should().Be(reviewerId);
    }

    [Fact]
    public async Task Approve_RevalidatesOverlap_ReturnsWarning()
    {
        var (es, rota, shift1) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var shift2 = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        var userId = Guid.NewGuid();
        // User has confirmed signup for shift1
        SeedSignup(userId, shift1.Id, SignupStatus.Confirmed);
        // User has pending signup for shift2 (same time slot)
        var pendingSignup = SeedSignup(userId, shift2.Id, SignupStatus.Pending);
        var reviewerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveAsync(pendingSignup.Id, reviewerId);

        // Approve succeeds (overlap is a warning, not a blocker at approval)
        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("Time conflict");
    }

    // ============================================================
    // Bail
    // ============================================================

    [Fact]
    public async Task Bail_FromConfirmed_SetsBailed()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        var result = await _service.BailAsync(signup.Id, userId, "Can't make it");

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.Bailed);
        result.Signup.StatusReason.Should().Be("Can't make it");
    }

    [Fact]
    public async Task Bail_BuildShift_AfterEeClose_NonPrivileged_ReturnsError()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Make this a build shift (day offset < 0)
        shift.DayOffset = -1;
        // Set EE close to the past
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        var result = await _service.BailAsync(signup.Id, userId, null);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("early entry close");
    }

    // ============================================================
    // Voluntell
    // ============================================================

    [Fact]
    public async Task Voluntell_CreatesConfirmedWithEnrolledFlag()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.Confirmed);
        result.Signup.Enrolled.Should().BeTrue();
        result.Signup.EnrolledByUserId.Should().Be(enrollerId);
        result.Signup.ReviewedByUserId.Should().Be(enrollerId);
    }

    // ============================================================
    // MarkNoShow
    // ============================================================

    [Fact]
    public async Task MarkNoShow_BeforeShiftEnd_ReturnsError()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Shift at day 1, 10:00, 4h duration → ends at 14:00 on gate opening + 1 day
        // Gate opening: 2026-07-01 → shift is 2026-07-02 10:00-14:00 Madrid
        // TestNow is 2026-06-15 12:00 UTC → before the shift even starts
        // But let's make a shift that ends in the future:
        // Use day offset 0 (gate opening day), start 10:00, 4h → ends 14:00
        // In Madrid timezone (UTC+2 in summer), that's 12:00 UTC
        // TestNow is 12:00 UTC, so shift end is at 12:00 UTC — we need it to be after now
        // Let's set gate opening to 2026-06-15, shift at day 0, start 14:00 → ends 18:00 local = 16:00 UTC
        es.GateOpeningDate = new LocalDate(2026, 6, 15);
        shift.DayOffset = 0;
        shift.StartTime = new LocalTime(14, 0);

        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        var reviewerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.MarkNoShowAsync(signup.Id, reviewerId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("before the shift ends");
    }

    [Fact]
    public async Task MarkNoShow_AfterShiftEnd_SetsNoShow()
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Make shift end in the past:
        // Gate opening = 2026-06-14, shift day 0, start 08:00, 2h → ends 10:00 local = 08:00 UTC
        // TestNow = 2026-06-15 12:00 UTC → well past shift end
        es.GateOpeningDate = new LocalDate(2026, 6, 14);
        shift.DayOffset = 0;
        shift.StartTime = new LocalTime(8, 0);
        shift.Duration = Duration.FromHours(2);

        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        var reviewerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        var result = await _service.MarkNoShowAsync(signup.Id, reviewerId);

        result.Success.Should().BeTrue();
        result.Signup!.Status.Should().Be(SignupStatus.NoShow);
    }

    // ============================================================
    // SignUpRange
    // ============================================================

    [Fact]
    public async Task SignUpRange_CreatesOneSignupPerDay()
    {
        // Arrange: rota with 5 all-day shifts (days -5 to -1), user picks days -3 to -1
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        // Add 5 all-day shifts
        for (var day = -5; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1);

        // Assert: 3 signups created, all share same SignupBlockId
        result.Success.Should().BeTrue();
        var signups = await _dbContext.ShiftSignups
            .Where(s => s.UserId == userId)
            .ToListAsync();
        signups.Should().HaveCount(3);
        signups.Should().AllSatisfy(s => s.SignupBlockId.Should().NotBeNull());
        signups.Select(s => s.SignupBlockId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task SignUpRange_BlocksIfAnyDayOverlaps()
    {
        // Arrange: user already has a confirmed signup on day -2
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -5; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        // Find the day -2 shift and create an existing signup
        await _dbContext.SaveChangesAsync();
        var dayMinus2Shift = await _dbContext.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2);
        SeedSignup(userId, dayMinus2Shift.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        // Act: try to sign up for days -3 to -1
        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1);

        // Assert: fails — duplicate signup check catches this before overlap check
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Already signed up");
    }

    // ============================================================
    // BailRange
    // ============================================================

    [Fact]
    public async Task BailRange_BailsAllSignupsInBlock()
    {
        // Arrange: user signed up for days -3 to -1 (shared SignupBlockId)
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        var shifts = new List<Shift>();
        for (var day = -3; day <= -1; day++)
            shifts.Add(SeedAllDayShift(rota, day));
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        foreach (var shift in shifts)
        {
            var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
            signup.SignupBlockId = blockId;
        }
        await _dbContext.SaveChangesAsync();

        // Act
        await _service.BailRangeAsync(blockId, userId);

        // Assert: all 3 signups now Bailed
        var signups = await _dbContext.ShiftSignups
            .Where(s => s.SignupBlockId == blockId)
            .ToListAsync();
        signups.Should().HaveCount(3);
        signups.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Bailed));
    }

    // ============================================================
    // VoluntellRange
    // ============================================================

    [Fact]
    public async Task VoluntellRange_CreatesConfirmedSignupsAcrossDateRange()
    {
        // Arrange: rota with 3 all-day shifts (days -3 to -1)
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        // Assert
        result.Success.Should().BeTrue();
        var signups = await _dbContext.ShiftSignups
            .Where(s => s.UserId == volunteerId)
            .ToListAsync();
        signups.Should().HaveCount(3);
        signups.Should().AllSatisfy(s =>
        {
            s.Status.Should().Be(SignupStatus.Confirmed);
            s.Enrolled.Should().BeTrue();
            s.EnrolledByUserId.Should().Be(enrollerId);
            s.SignupBlockId.Should().NotBeNull();
        });
        signups.Select(s => s.SignupBlockId).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task VoluntellRange_SkipsShiftsWhereUserAlreadySignedUp()
    {
        // Arrange: rota with 3 all-day shifts, user already signed up on day -2
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await _dbContext.SaveChangesAsync();

        // Pre-existing signup on day -2
        var dayMinus2Shift = await _dbContext.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2);
        SeedSignup(volunteerId, dayMinus2Shift.Id, SignupStatus.Confirmed);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        // Assert: only 2 new signups (day -3 and day -1), skipping day -2
        result.Success.Should().BeTrue();
        var newSignups = await _dbContext.ShiftSignups
            .Where(s => s.UserId == volunteerId && s.Enrolled)
            .ToListAsync();
        newSignups.Should().HaveCount(2);
    }

    [Fact]
    public async Task VoluntellRange_ReturnsError_WhenRotaNotFound()
    {
        // Act
        var result = await _service.VoluntellRangeAsync(Guid.NewGuid(), Guid.NewGuid(), -3, -1, Guid.NewGuid());

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Rota not found");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private (EventSettings es, Rota rota, Shift shift) SeedShiftScenario(
        SignupPolicy policy,
        ShiftPriority priority = ShiftPriority.Normal)
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
        _dbContext.EventSettings.Add(es);

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
        _dbContext.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = "Test Rota",
            Priority = priority,
            Policy = policy,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Rotas.Add(rota);

        // Set navigation properties for in-memory provider
        rota.EventSettings = es;
        rota.Team = team;

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
            UpdatedAt = TestNow
        };
        shift.Rota = rota;
        _dbContext.Shifts.Add(shift);
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
            StartTime = new LocalTime(0, 0),
            Duration = Duration.FromHours(24),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        shift.Rota = rota;
        _dbContext.Shifts.Add(shift);
        return shift;
    }

    private ShiftSignup SeedSignup(Guid userId, Guid shiftId, SignupStatus status)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.ShiftSignups.Add(signup);
        return signup;
    }
}

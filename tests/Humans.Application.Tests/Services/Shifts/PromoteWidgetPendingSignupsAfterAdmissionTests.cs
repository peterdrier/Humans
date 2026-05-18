using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
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
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class PromoteWidgetPendingSignupsAfterAdmissionTests : ServiceTestHarness
{
    private readonly IAuditLogService _auditLog;
    private readonly IMembershipCalculator _membership;
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftSignupRepository _repo;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly EventSettings _activeEvent;
    private readonly Team _team;

    public PromoteWidgetPendingSignupsAfterAdmissionTests()
        : base(TestNow)
    {
        _auditLog = Substitute.For<IAuditLogService>();
        _membership = Substitute.For<IMembershipCalculator>();
        // Default: user has all required consents — exercises the existing
        // promotion logic. Tests that need the missing-consents guard override
        // this on a per-test basis.
        _membership.HasAllRequiredConsentsForTeamAsync(
            Arg.Any<Guid>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(true);

        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(teamService);
        serviceProvider.GetService(typeof(IRoleAssignmentService)).Returns(roleAssignmentService);

        var shiftRepo = new ShiftManagementRepository(DbFactory);

        _shiftMgmt = new ShiftManagementService(
            shiftRepo,
            _auditLog,
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        _repo = new ShiftSignupRepository(Db, Clock);
        _service = new ShiftSignupService(
            _repo,
            _shiftMgmt,
            _membership,
            _auditLog,
            Substitute.For<INotificationService>(),
            Substitute.For<IAdminAuthorizationService>(),
            Substitute.For<IShiftViewInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance);

        _activeEvent = new EventSettings
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
        Db.EventSettings.Add(_activeEvent);

        _team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Department",
            Slug = "test-dept",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(_team);
        Db.SaveChanges();
    }

    [HumansFact]
    public async Task Promote_PublicRotaPendingSignup_BecomesConfirmed()
    {
        var signup = SeedPendingSignup(SignupPolicy.Public, capacity: 5, confirmedSoFar: 0);

        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

        var reloaded = await Db.ShiftSignups.AsNoTracking()
            .FirstAsync(s => s.Id == signup.Id);
        Assert.Equal(SignupStatus.Confirmed, reloaded.Status);
    }

    [HumansFact]
    public async Task Promote_MissingRequiredConsents_StaysPending()
    {
        // ConsentService.SubmitConsentAsync calls promote after every signature.
        // Until ALL required Volunteer consents are signed, signups must remain
        // Pending — Confirmed implies admitted Volunteer.
        _membership.HasAllRequiredConsentsForTeamAsync(
            _userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(false);
        var signup = SeedPendingSignup(SignupPolicy.Public, capacity: 5, confirmedSoFar: 0);

        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

        var reloaded = await Db.ShiftSignups.AsNoTracking()
            .FirstAsync(s => s.Id == signup.Id);
        Assert.Equal(SignupStatus.Pending, reloaded.Status);
    }

    [HumansFact]
    public async Task Promote_RequireApprovalPendingSignup_StaysPending()
    {
        var signup = SeedPendingSignup(SignupPolicy.RequireApproval, capacity: 5, confirmedSoFar: 0);

        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

        var reloaded = await Db.ShiftSignups.AsNoTracking()
            .FirstAsync(s => s.Id == signup.Id);
        Assert.Equal(SignupStatus.Pending, reloaded.Status);
    }

    [HumansFact]
    public async Task Promote_PublicBuildRangeBlock_AllShiftsConfirmed()
    {
        var blockId = Guid.NewGuid();
        var ids = SeedPendingBlock(blockId, SignupPolicy.Public, dayOffsets: [-3, -2, -1]);

        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

        var block = await Db.ShiftSignups.AsNoTracking()
            .Where(s => s.SignupBlockId == blockId)
            .ToListAsync();
        Assert.Equal(3, block.Count);
        Assert.All(block, s => Assert.Equal(SignupStatus.Confirmed, s.Status));
        Assert.All(block, s => Assert.Contains(s.Id, ids));
    }

    [HumansFact]
    public async Task Promote_PublicShiftFilledSinceCreation_StaysPending()
    {
        // Capacity 1, one Confirmed already → user's Pending cannot promote.
        var signup = SeedPendingSignup(SignupPolicy.Public, capacity: 1, confirmedSoFar: 1);

        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

        var reloaded = await Db.ShiftSignups.AsNoTracking()
            .FirstAsync(s => s.Id == signup.Id);
        Assert.Equal(SignupStatus.Pending, reloaded.Status);
    }

    [HumansFact]
    public async Task Promote_NoPendingSignups_DoesNotThrow()
    {
        await _service.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);
    }

    private ShiftSignup SeedPendingSignup(SignupPolicy policy, int capacity, int confirmedSoFar)
    {
        var rota = SeedRota(policy);
        var shift = SeedShift(rota, dayOffset: 1, capacity);

        for (var i = 0; i < confirmedSoFar; i++)
        {
            Db.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                ShiftId = shift.Id,
                Status = SignupStatus.Confirmed,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });
        }

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            ShiftId = shift.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.ShiftSignups.Add(signup);
        Db.SaveChanges();
        return signup;
    }

    private HashSet<Guid> SeedPendingBlock(Guid blockId, SignupPolicy policy, int[] dayOffsets)
    {
        var rota = SeedRota(policy);
        rota.Period = RotaPeriod.Build;

        var ids = new HashSet<Guid>();
        foreach (var dayOffset in dayOffsets)
        {
            var shift = SeedShift(rota, dayOffset, capacity: 5, isAllDay: true);
            var signup = new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                ShiftId = shift.Id,
                SignupBlockId = blockId,
                Status = SignupStatus.Pending,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            };
            Db.ShiftSignups.Add(signup);
            ids.Add(signup.Id);
        }
        Db.SaveChanges();
        return ids;
    }

    private Rota SeedRota(SignupPolicy policy)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _activeEvent.Id,
            TeamId = _team.Id,
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = policy,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = _activeEvent
        };
        Db.Rotas.Add(rota);
        return rota;
    }

    private Shift SeedShift(Rota rota, int dayOffset, int capacity, bool isAllDay = false)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = isAllDay,
            StartTime = isAllDay ? LocalTime.Midnight : new LocalTime(10, 0),
            Duration = isAllDay ? Duration.FromHours(24) : Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = capacity,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };
        Db.Shifts.Add(shift);
        return shift;
    }
}

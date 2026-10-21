using System.Globalization;
using System.Resources;
using Humans.Shifts.Domain;
using Humans.Auth.Contracts;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Humans.Tickets.Contracts;
using Humans.Camps.Contracts;
using NSubstitute;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Base.Enums;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using ShiftSignupService = Humans.Shifts.Services.ShiftSignupService;
using Humans.Teams.Contracts;
using Humans.Notifications.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Xunit;
using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Tests.Services;

public sealed class ShiftSignupServiceTests : ShiftsTestHarness
{
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftRepository _repo;
    private readonly ShiftSignupService _service;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftViewInvalidator _viewInvalidator;
    private readonly IStringLocalizer<ShiftsResource> _localizer = Substitute.For<IStringLocalizer<ShiftsResource>>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    // Fixed test time: 2026-06-15 12:00 UTC
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupServiceTests()
        : base(TestNow)
    {
        _teamService = Substitute.For<ITeamService>();
        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfoStubHelpers.MakeUserInfo(call.Arg<Guid>()) with { State = UserState.Active });
        var serviceProvider = new ServiceLocatorBuilder()
            .With(_teamService)
            .With(_teamService)
            .With(_roleAssignmentService)
            .With<ITicketService>()
            .With(_users)
            .With<ICampService>()
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
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        _repo = new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock);
        _viewInvalidator = Substitute.For<IShiftViewInvalidator>();
        _localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var resources = new ResourceManager(typeof(ShiftsResource));
        var english = CultureInfo.GetCultureInfo("en");
        foreach (var key in new[] {
            "Shifts_Signup_RangeDuplicate",
            "Shifts_Signup_RangeDuplicateWarning",
            "Shifts_Signup_RangeConflictDays",
            "Shifts_Signup_RangeCapacityDays",
            "Shifts_Signup_RangeEarlyEntryCapacityDays",
            "Shifts_Signup_RangeNothingToAdd",
            "Shifts_Signup_TimeConflict",
            "Shifts_Signup_UnknownTeam",
        })
        {
            _localizer[key].Returns(new LocalizedString(key, resources.GetString(key, english)!));
            _localizer[key, Arg.Any<object[]>()].Returns(call => new LocalizedString(key,
                string.Format(english, resources.GetString(key, english)!, call.Arg<object[]>())));
        }
        _service = new ShiftSignupService(
            _repo,
            Substitute.For<IVolunteerTrackingRepository>(),
            _shiftMgmt,
            NewCalendarResolver(),
            AuditLog,
            Notifier,
            AdminAuthorization,
            _viewInvalidator,
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance,
            _users,
            _localizer);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignupHistoryRead_CanceledToken_StopsRepositoryRead(bool noShowHistory)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        Func<Task> read = noShowHistory
            ? () => _service.GetNoShowHistoryAsync(Guid.NewGuid(), aborted.Token)
            : () => _service.GetByUserAsync(Guid.NewGuid(), ct: aborted.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task ContributeForUserAsync_IncludesSignupLastUpdateWithoutAReview()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        ShiftsDb.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Status = SignupStatus.Cancelled,
            CreatedAt = TestNow,
            UpdatedAt = TestNow + Duration.FromHours(1),
            StatusReason = "Shift cancelled"
        });
        await ShiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);
        var slice = slices.Single(x => string.Equals(x.SectionName, "ShiftSignups", StringComparison.Ordinal));
        var signup = System.Text.Json.JsonSerializer.SerializeToElement(slice.Data).EnumerateArray().Single();
        signup.GetProperty("CreatedAt").GetString().Should().Be("2026-06-15T12:00:00Z");
        signup.GetProperty("UpdatedAt").GetString().Should().Be("2026-06-15T13:00:00Z");
        signup.GetProperty("ReviewedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        signup.GetProperty("StatusReason").GetString().Should().Be("Shift cancelled");
    }

    [HumansTheory]
    [InlineData("signup", "signup", true)]
    [InlineData("signup", "signup", false)]
    [InlineData("range", "voluntell-range", true)]
    [InlineData("voluntell", "range", true)]
    [InlineData("approve", "signup", false)]
    [InlineData("approve-range", "signup", false)]
    public async Task ConcurrentEnrollment_PreservesDuplicateAndCapacityGuards(
        string firstPath, string secondPath, bool sameUser)
    {
        var ct = TestContext.Current.CancellationToken;
        var calendar = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true
        };
        ShiftsDb.EventSettings.Add(calendar);
        await ShiftsDb.SaveChangesAsync(ct);
        var pause = new PauseFirstEnrollmentSave();
        var options = new DbContextOptionsBuilder<ShiftsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(pause).Options;
        var factory = new TestDbContextFactory<ShiftsDbContext>(options);
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = calendar.Id,
            TeamId = Guid.NewGuid(),
            Name = "Event rota",
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event
        };
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 1,
            IsAllDay = true,
            MinVolunteers = 0,
            MaxVolunteers = sameUser ? 2 : 1
        };
        var firstUserId = Guid.NewGuid();
        var secondUserId = sameUser ? firstUserId : Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        await using (var seed = new ShiftsDbContext(options))
        {
            seed.EventSettings.Add(new EventSettings { Id = calendar.Id, IsShiftBrowsingOpen = true });
            seed.Rotas.Add(rota);
            seed.Shifts.Add(shift);
            if (firstPath.StartsWith("approve", StringComparison.Ordinal))
                seed.ShiftSignups.Add(new ShiftSignup
                {
                    Id = pendingId,
                    ShiftId = shift.Id,
                    UserId = firstUserId,
                    SignupBlockId = blockId,
                    Status = SignupStatus.Pending,
                    CreatedAt = TestNow,
                    UpdatedAt = TestNow
                });
            await seed.SaveChangesAsync(ct);
        }
        pause.Armed = true;
        await using var firstDb = new ShiftsDbContext(options);
        await using var secondDb = new ShiftsDbContext(options);
        var services = new ServiceLocatorBuilder().With(_teamService).Build();
        ShiftSignupService NewService(ShiftsDbContext db) => new(
            new ShiftRepository(factory, db, Clock), new VolunteerTrackingRepository(db),
            Substitute.For<IShiftManagement>(), NewCalendarResolver(), AuditLog, Notifier,
            AdminAuthorization, _viewInvalidator, services, Clock,
            NullLogger<ShiftSignupService>.Instance, _users, _localizer);
        Task<SignupResult> Enroll(ShiftSignupService service, string path, Guid userId) => path switch
        {
            "signup" => service.SignUpAsync(userId, shift.Id),
            "range" => service.SignUpRangeAsync(userId, rota.Id, 1, 1),
            "voluntell" => service.VoluntellAsync(userId, shift.Id, Guid.NewGuid()),
            "voluntell-range" => service.VoluntellRangeAsync(userId, rota.Id, 1, 1, Guid.NewGuid()),
            "approve" => service.ApproveAsync(pendingId, Guid.NewGuid()),
            _ => service.ApproveRangeAsync(blockId, Guid.NewGuid())
        };
        var first = Enroll(NewService(firstDb), firstPath, firstUserId);
        Task<SignupResult> second;
        try
        {
            await Task.WhenAny(first, pause.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (first.IsCompleted) await first;
            pause.Entered.Task.IsCompleted.Should().BeTrue("the first enrollment save must be paused");
            second = Enroll(NewService(secondDb), secondPath, secondUserId);
        }
        finally
        {
            pause.Resume.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);

        await using var stored = new ShiftsDbContext(options);
        var signup = (await stored.ShiftSignups.ToListAsync(ct)).Should().ContainSingle().Subject;
        signup.UserId.Should().Be(firstUserId);
        signup.Status.Should().Be(SignupStatus.Confirmed);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeFalse();
        results[1].Error.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class PauseFirstEnrollmentSave : SaveChangesInterceptor
    {
        private int _paused;
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<ShiftSignup>().Any(entry =>
                    entry.State == EntityState.Added
                    || (entry.State == EntityState.Modified && entry.Entity.Status == SignupStatus.Confirmed))
                && Interlocked.CompareExchange(ref _paused, 1, 0) == 0)
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [HumansTheory]
    [InlineData("blocked")]
    [InlineData("partial")]
    [InlineData("empty")]
    public async Task SignUpRange_LocalizesDuplicateAndEmptySummaries(string scenario)
    {
        var resources = new ResourceManager(typeof(ShiftsResource));
        var spanish = CultureInfo.GetCultureInfo("es");
        foreach (var key in new[] { "Shifts_Signup_RangeDuplicate", "Shifts_Signup_RangeNothingToAdd" })
            _localizer[key].Returns(new LocalizedString(key, resources.GetString(key, spanish)!));
        const string warningKey = "Shifts_Signup_RangeDuplicateWarning";
        _localizer[warningKey, Arg.Any<object[]>()].Returns(call => new LocalizedString(warningKey,
            string.Format(spanish, resources.GetString(warningKey, spanish)!, call.Arg<object[]>())));
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        var first = SeedAllDayShift(rota, -3);
        if (string.Equals(scenario, "partial", StringComparison.Ordinal))
            SeedAllDayShift(rota, -2);
        var userId = Guid.NewGuid();
        SeedSignup(userId, first.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -2,
            flags: string.Equals(scenario, "blocked", StringComparison.Ordinal)
                ? ShiftSignupRequestFlags.None : ShiftSignupRequestFlags.SkipConflicts);

        if (string.Equals(scenario, "blocked", StringComparison.Ordinal))
            result.Error.Should().Be(resources.GetString("Shifts_Signup_RangeDuplicate", spanish));
        else if (string.Equals(scenario, "empty", StringComparison.Ordinal))
            result.Error.Should().Contain(resources.GetString("Shifts_Signup_RangeNothingToAdd", spanish)!);
        else
        {
            result.Success.Should().BeTrue();
            result.Warning.Should().StartWith("Ya te has apuntado para estos días:");
        }
    }

    // ============================================================
    // SignUp
    // ============================================================

    [HumansTheory]
    [InlineData(null, false)]
    [InlineData(UserState.Bare, false)]
    [InlineData(UserState.Suspended, false)]
    [InlineData(UserState.AdminSuspended, false)]
    [InlineData(UserState.Rejected, false)]
    [InlineData(UserState.DeletePending, false)]
    [InlineData(UserState.Deleted, false)]
    [InlineData(UserState.Merged, false)]
    [InlineData(null, true)]
    [InlineData(UserState.Bare, true)]
    [InlineData(UserState.Suspended, true)]
    [InlineData(UserState.AdminSuspended, true)]
    [InlineData(UserState.Rejected, true)]
    [InlineData(UserState.DeletePending, true)]
    [InlineData(UserState.Deleted, true)]
    [InlineData(UserState.Merged, true)]
    public async Task SelfSignup_NonActiveAccount_DoesNotWriteOrNotify(UserState? state, bool range)
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(state is { } actualState
                ? UserInfoStubHelpers.MakeUserInfo(userId) with { State = actualState }
                : null);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        foreach (var flags in new[]
        {
            ShiftSignupRequestFlags.None,
            ShiftSignupRequestFlags.Privileged,
            ShiftSignupRequestFlags.SkipConflicts,
            ShiftSignupRequestFlags.Privileged | ShiftSignupRequestFlags.SkipConflicts,
        })
        {
            var result = range
                ? await _service.SignUpRangeAsync(userId, rota.Id, -3, -1, userId, flags)
                : await _service.SignUpAsync(userId, shift.Id, userId, flags);

            result.Success.Should().BeFalse($"{state?.ToString() ?? "missing user"} cannot self-sign up with {flags}");
            result.Error.Should().Be("Shifts_AccountCannotSignUp");
            (await ShiftsDb.ShiftSignups.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
            AuditLog.ReceivedCalls().Should().BeEmpty();
            Notifier.ReceivedCalls().Should().BeEmpty();
            _viewInvalidator.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignUp_PublicPolicy_CreatesConfirmed(bool mergedAlias)
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        _users.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(userId) with { State = UserState.Active });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(inputId, shift.Id);

        result.Success.Should().BeTrue();
        Saved(result).UserId.Should().Be(userId);
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);
        Saved(result).ReviewedByUserId.Should().Be(userId);
    }

    [HumansFact]
    public async Task SignUp_RequireApprovalPolicy_CreatesPending()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Pending);
        Saved(result).ReviewedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task SignUp_DuplicateSignup_ReturnsError()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        const string key = "Shifts_Signup_AlreadySignedUp";
        _localizer[key].Returns(new LocalizedString(key, "translated duplicate rejection"));
        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("translated duplicate rejection");
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task SignUp_OverlappingShift_ReturnsError(string cultureName)
    {
        var (es, rota, shift1) = SeedShiftScenario(SignupPolicy.Public);
        // Create a second shift at overlapping time (same day, same start)
        var shift2 = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        var userId = Guid.NewGuid();
        SeedSignup(userId, shift1.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        using var culture = new CultureScope(cultureName);
        LocalizeSignupMessages(cultureName);
        // shift1: day 1, 10:00-14:00 — shift2: day 1, 10:00-14:00 (identical times)
        var result = await _service.SignUpAsync(userId, shift2.Id);

        result.Success.Should().BeFalse();
        var resources = new ResourceManager(typeof(ShiftsResource));
        var expected = string.Format(CultureInfo.CurrentUICulture,
            resources.GetString("Shifts_Signup_TimeConflict", CultureInfo.CurrentUICulture)!,
            rota.Name, resources.GetString("Shifts_Signup_UnknownTeam", CultureInfo.CurrentUICulture),
            es.GateOpeningDate.PlusDays(shift1.DayOffset).At(shift1.StartTime)
                .ToDateTimeUnspecified().ToMonthDayTime());
        result.Error.Should().Be(expected);
    }

    [HumansFact]
    public async Task SignUp_AllDayShiftAfterPriorNightWatch_DoesNotFalselyConflict()
    {
        // All-day rows resolve their absolute window to 08:00/18:00, so a night watch
        // ending at 02:00 does not overlap the next day's all-day shift.
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Strike;
        // Night watch: day 0, 22:00-02:00 (next day) — 4h
        var nightWatch = SeedShift(rota, dayOffset: 0, startHour: 22, durationHours: 4);
        // All-day strike shift on the following day; stored as midnight/24h sentinel (don't-care)
        var allDay = SeedAllDayShift(rota, dayOffset: 1);
        var userId = Guid.NewGuid();
        SeedSignup(userId, nightWatch.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, allDay.Id);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [HumansFact]
    public async Task SignUp_SameDayEarlyShiftBeforeAllDay_DoesNotFalselyConflict()
    {
        // All-day shifts resolve to an 08:00-18:00 window, so an early-morning shift
        // (03:00-07:00) on the same calendar day has no overlap.
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Strike;
        // Early shift: day 0, 03:00-07:00 — ends one hour before all-day window starts
        var earlyShift = SeedShift(rota, dayOffset: 0, startHour: 3, durationHours: 4);
        // All-day shift on the same day (08:00-18:00 computed)
        var allDay = SeedAllDayShift(rota, dayOffset: 0);
        var userId = Guid.NewGuid();
        SeedSignup(userId, allDay.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, earlyShift.Id);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [HumansFact]
    public async Task SignUp_SystemClosed_RegularVolunteer_ReturnsError()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        es.IsShiftBrowsingOpen = false;
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_BrowsingClosed");
    }

    [HumansFact]
    public async Task SignUp_AdminOnlyShift_RegularVolunteer_ReturnsError()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.AdminOnly = true;
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_Restricted");
    }

    [HumansFact]
    public async Task SignUp_ShiftAtMaxVolunteers_ReturnsError()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        for (var i = 0; i < shift.MaxVolunteers; i++)
            SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(Guid.NewGuid(), shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_AtCapacity");
        (await ShiftsDb.ShiftSignups.CountAsync(
            s => s.ShiftId == shift.Id && s.Status == SignupStatus.Confirmed,
            TestContext.Current.CancellationToken)).Should().Be(shift.MaxVolunteers);
    }

    [HumansFact]
    public async Task SignUp_BuildShiftAfterEeClose_NonPrivileged_ReturnsError()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.DayOffset = -1;
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_EarlyEntryClosed");
    }

    [HumansFact]
    public async Task SignUp_BuildShiftAfterEeClose_Privileged_Succeeds()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.DayOffset = -1;
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id, flags: ShiftSignupRequestFlags.Privileged);

        result.Success.Should().BeTrue();
    }

    // ============================================================
    // Approve
    // ============================================================

    [HumansFact]
    public async Task Approve_FromPending_SetsConfirmed()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Pending);
        var reviewerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(signup.Id, reviewerId);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);
        Saved(result).ReviewedByUserId.Should().Be(reviewerId);
    }

    [HumansFact]
    public async Task Approve_RevalidatesOverlap_ReturnsWarning()
    {
        var (_, rota, shift1) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var shift2 = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        var userId = Guid.NewGuid();
        // User has confirmed signup for shift1
        SeedSignup(userId, shift1.Id, SignupStatus.Confirmed);
        // User has pending signup for shift2 (same time slot)
        var pendingSignup = SeedSignup(userId, shift2.Id, SignupStatus.Pending);
        var reviewerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(pendingSignup.Id, reviewerId);

        // Approve succeeds (overlap is a warning, not a blocker at approval)
        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("Time conflict");
    }

    [HumansFact]
    public async Task Approve_ShiftAtMaxVolunteers_ReturnsError()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        for (var i = 0; i < shift.MaxVolunteers; i++)
            SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        var pendingSignup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Pending);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(pendingSignup.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("at capacity");
        (await ShiftsDb.ShiftSignups.SingleAsync(s => s.Id == pendingSignup.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(SignupStatus.Pending);
    }

    [HumansFact]
    public async Task Approve_BuildShiftAfterEeClose_NonPrivileged_ReturnsError()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        shift.DayOffset = -1;
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var signup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Pending);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(signup.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("early entry close");
    }

    [HumansFact]
    public async Task Approve_BuildShiftAfterEeClose_Privileged_Succeeds()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        shift.DayOffset = -1;
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var signup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Pending);
        var reviewerId = Guid.NewGuid();
        _roleAssignmentService.HasActiveRoleAsync(reviewerId, RoleNames.Admin).Returns(true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(signup.Id, reviewerId);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);
    }

    // ============================================================
    // Bail
    // ============================================================

    [HumansFact]
    public async Task Bail_FromConfirmed_SetsBailed()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.BailAsync(signup.Id, userId, "Can't make it");

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Bailed);
        Saved(result).StatusReason.Should().Be("Can't make it");
    }

    [HumansTheory]
    [InlineData(SignupStatus.Refused, "Shifts_Bail_NotActive")]
    [InlineData(SignupStatus.Cancelled, "Shifts_Bail_NotActive")]
    [InlineData(SignupStatus.NoShow, "Shifts_Bail_NotActive")]
    [InlineData(SignupStatus.Bailed, "Shifts_Bail_AlreadyBailed")]
    public async Task Bail_TerminalSignup_ReturnsLocalizedRefusalWithoutChangingHistory(
        SignupStatus status, string key)
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, status);
        signup.StatusReason = "Previous decision";
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _localizer[key].Returns(new LocalizedString(key, "translated rejection"));

        var result = await _service.BailAsync(signup.Id, userId, "stale form");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("translated rejection");
        var stored = await ShiftsDb.ShiftSignups.FindAsync([signup.Id], TestContext.Current.CancellationToken);
        stored!.Status.Should().Be(status);
        stored.StatusReason.Should().Be("Previous decision");
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Notifier.ReceivedCalls().Should().BeEmpty();
        _viewInvalidator.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("refuse", 1000)]
    [InlineData("refuse", 1001)]
    [InlineData("bail", 1000)]
    [InlineData("bail", 1001)]
    [InlineData("remove", 1000)]
    [InlineData("remove", 1001)]
    [InlineData("refuseRange", 1000)]
    [InlineData("refuseRange", 1001)]
    [InlineData("bailRange", 1000)]
    [InlineData("bailRange", 1001)]
    public async Task Status_reason_limit_is_checked_before_any_signup_changes(string operation, int length)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var isRefusal = operation.StartsWith("refuse", StringComparison.Ordinal);
        var initialStatus = isRefusal ? SignupStatus.Pending : SignupStatus.Confirmed;
        var signup = SeedSignup(userId, shift.Id, initialStatus);
        var signups = new List<ShiftSignup> { signup };
        var blockId = Guid.NewGuid();
        signup.SignupBlockId = blockId;
        if (operation.EndsWith("Range", StringComparison.Ordinal))
        {
            var second = SeedSignup(userId, SeedAllDayShift(rota, 1).Id, initialStatus);
            second.SignupBlockId = blockId;
            signups.Add(second);
        }
        await SaveAllAsync(ct);
        var reason = new string('x', length);
        bool success;
        string? error;
        if (string.Equals(operation, "bailRange", StringComparison.Ordinal))
        {
            var result = await _service.BailRangeAsync(blockId, userId, reason);
            success = result.ErrorKey is null;
            error = result.ErrorKey;
        }
        else
        {
            var result = operation switch
            {
                "refuse" => await _service.RefuseAsync(signup.Id, userId, reason),
                "bail" => await _service.BailAsync(signup.Id, userId, reason),
                "remove" => await _service.RemoveSignupAsync(signup.Id, userId, reason),
                _ => await _service.RefuseRangeAsync(blockId, userId, reason)
            };
            success = result.Success;
            error = result.Error;
        }

        success.Should().Be(length <= 1000);
        var ids = signups.Select(s => s.Id).ToList();
        var stored = await ShiftsDb.ShiftSignups.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        if (length > 1000)
        {
            error.Should().Be("Shifts_StatusReasonTooLong");
            stored.Should().AllSatisfy(s =>
            {
                s.Status.Should().Be(initialStatus);
                s.StatusReason.Should().BeNull();
            });
            AuditLog.ReceivedCalls().Should().BeEmpty();
            _viewInvalidator.ReceivedCalls().Should().BeEmpty();
            Notifier.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            var expected = isRefusal ? SignupStatus.Refused
                : string.Equals(operation, "remove", StringComparison.Ordinal) ? SignupStatus.Cancelled : SignupStatus.Bailed;
            stored.Should().AllSatisfy(s =>
            {
                s.Status.Should().Be(expected);
                s.StatusReason.Should().Be(reason);
            });
            AuditLog.ReceivedCalls().Should().HaveCount(signups.Count);
            AuditLog.ReceivedCalls().Should().OnlyContain(call =>
                ((string)call.GetArguments()[3]!).Contains(reason, StringComparison.Ordinal));
        }
    }

    [HumansFact]
    public async Task Bail_BuildShift_AfterEeClose_NonPrivileged_ReturnsError()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Make this a build shift (day offset < 0)
        shift.DayOffset = -1;
        // Set EE close to the past
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.BailAsync(signup.Id, userId, null);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Bail_EarlyEntryClosed");
    }

    // ============================================================
    // ToggleDay
    // ============================================================

    [HumansFact]
    public async Task ToggleDay_NoExistingSignup_HasDietaryPreference_SignsUp()
    {
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        var shift = SeedAllDayShift(rota, dayOffset: 1); // qualifies for cantina meal
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: false, hasDietaryPreference: true);

        outcome.NeedsDietaryFirst.Should().BeFalse();
        outcome.SignedUp.Should().BeTrue();
        outcome.Result!.Success.Should().BeTrue();
        outcome.SignupsAfter.Should().ContainSingle(s => s.ShiftId == shift.Id && s.Status == SignupStatus.Confirmed);
    }

    [HumansFact]
    public async Task ToggleDay_NoExistingSignup_QualifyingShift_NoDietaryPreference_ReturnsNeedsDietaryFirst()
    {
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        var shift = SeedAllDayShift(rota, dayOffset: 1); // qualifies for cantina meal
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: false, hasDietaryPreference: false);

        outcome.NeedsDietaryFirst.Should().BeTrue();
        outcome.Result.Should().BeNull();
        var signupCount = await ShiftsDb.ShiftSignups.CountAsync(s => s.UserId == userId, TestContext.Current.CancellationToken);
        signupCount.Should().Be(0);
    }

    [HumansFact]
    public async Task ToggleDay_NoExistingSignup_NonQualifyingShift_NoDietaryPreference_SignsUp()
    {
        // 4h shift — below the 6h cantina-meal threshold and not all-day, so the
        // dietary gate never applies regardless of hasDietaryPreference.
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: false, hasDietaryPreference: false);

        outcome.NeedsDietaryFirst.Should().BeFalse();
        outcome.SignedUp.Should().BeTrue();
        outcome.Result!.Success.Should().BeTrue();
    }

    [HumansFact]
    public async Task ToggleDay_ExistingActiveSignup_Bails_RegardlessOfDietaryGate()
    {
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        var shift = SeedAllDayShift(rota, dayOffset: 1); // qualifies for cantina meal
        var userId = Guid.NewGuid();
        SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: false, hasDietaryPreference: false);

        outcome.NeedsDietaryFirst.Should().BeFalse();
        outcome.SignedUp.Should().BeFalse();
        outcome.Result!.Success.Should().BeTrue();
        Saved(outcome.Result).Status.Should().Be(SignupStatus.Bailed);
    }

    [HumansFact]
    public async Task ToggleDay_Privileged_CanViewRestrictedIsTrue()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: true, hasDietaryPreference: true);

        outcome.CanViewRestricted.Should().BeTrue();
    }

    [HumansFact]
    public async Task ToggleDay_NotPrivileged_NotCoordinator_CanViewRestrictedIsFalse()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.ToggleDayAsync(
            userId, shift.Id, es.Id, privileged: false, hasDietaryPreference: true);

        outcome.CanViewRestricted.Should().BeFalse();
    }

    // ============================================================
    // Voluntell
    // ============================================================

    [HumansTheory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "merged")]
    [InlineData(true, "merged")]
    public async Task Voluntell_InvalidPerson_DoesNotCreateSignup(bool range, string kind)
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        if (range)
        {
            rota.Period = RotaPeriod.Build;
            SeedAllDayShift(rota, -3);
        }
        var targetId = Guid.NewGuid();
        UserInfo? target = string.Equals(kind, "missing", StringComparison.Ordinal) ? null
            : UserInfoStubHelpers.MakeUserInfo(targetId) with
            {
                State = string.Equals(kind, "deleted", StringComparison.Ordinal) ? UserState.Deleted : UserState.Merged,
                MergedAt = Clock.GetCurrentInstant(),
            };
        _users.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(target));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = range
            ? await _service.VoluntellRangeAsync(targetId, rota.Id, -3, -3, Guid.NewGuid())
            : await _service.VoluntellAsync(targetId, shift.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_AccountCannotSignUp");
        (await ShiftsDb.ShiftSignups.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Voluntell_MergeAlias_UsesSurvivorWithoutNewAdmissionRestriction(bool range)
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        if (range)
        {
            rota.Period = RotaPeriod.Build;
            SeedAllDayShift(rota, -3);
        }
        var sourceId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        _users.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>()).Returns(
            UserInfoStubHelpers.MakeUserInfo(survivorId) with { State = UserState.Bare });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = range
            ? await _service.VoluntellRangeAsync(sourceId, rota.Id, -3, -3, Guid.NewGuid())
            : await _service.VoluntellAsync(sourceId, shift.Id, Guid.NewGuid());

        result.Success.Should().BeTrue();
        var signup = await ShiftsDb.ShiftSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        signup.UserId.Should().Be(survivorId);
        signup.Status.Should().Be(SignupStatus.Confirmed);
    }

    [HumansFact]
    public async Task Voluntell_CreatesConfirmedWithEnrolledFlag()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        _users.GetUserInfoAsync(volunteerId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(volunteerId) with { State = UserState.AdminSuspended });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);
        Saved(result).Enrolled.Should().BeTrue();
        Saved(result).EnrolledByUserId.Should().Be(enrollerId);
        Saved(result).ReviewedByUserId.Should().Be(enrollerId);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task VoluntellNotices_LongNamesDispatchFullCopy(int rangeCount)
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Name = new string('x', 168) + "😀" + new string('x', 86);
        if (rangeCount > 0)
        {
            rota.Period = RotaPeriod.Build;
            SeedAllDayShift(rota, -3);
            if (rangeCount > 1) SeedAllDayShift(rota, -2);
        }
        var volunteerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = rangeCount > 0
            ? await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -3 + rangeCount - 1, Guid.NewGuid())
            : await _service.VoluntellAsync(volunteerId, shift.Id, Guid.NewGuid());

        result.Success.Should().BeTrue();
        var notices = Notifier.ReceivedCalls().Where(call => call.GetArguments()[0] is NotificationSource)
            .Select(call => call.GetArguments()).ToList();
        notices.Should().NotBeEmpty();
        foreach (var args in notices)
        {
            if ((NotificationSource)args[0]! == NotificationSource.ShiftAssigned)
            {
                args[3].Should().Be(rangeCount switch
                {
                    0 => $"You were assigned to {rota.Name} on day {shift.DayOffset}",
                    1 => $"You were assigned to {rota.Name} (1 shift)",
                    _ => $"You were assigned to {rota.Name} ({rangeCount} shifts)"
                });
                args[5].Should().BeNull();
            }
            else
            {
                args[3].Should().Be($"Shift signup change: {rota.Name}");
                ((string)args[5]!).Should().Contain(rota.Name);
            }
        }
    }

    [HumansTheory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task VoluntellNotices_UseVolunteerLanguageWithLookupFailureFallback(int rangeCount, bool lookupFailure)
    {
        var range = rangeCount > 0;
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        if (range)
        {
            rota.Period = RotaPeriod.Build;
            SeedAllDayShift(rota, -3);
            if (rangeCount > 1) SeedAllDayShift(rota, -2);
        }
        var volunteerId = Guid.NewGuid();
        _users.GetUserInfoAsync(volunteerId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(volunteerId) with { PreferredLanguage = "es" });
        if (lookupFailure)
            _users.GetUserInfoAsync(volunteerId, Arg.Any<CancellationToken>())
                // Identity validation succeeds; the later best-effort language lookup fails.
                .Returns(new ValueTask<UserInfo?>(UserInfoStubHelpers.MakeUserInfo(volunteerId)),
                    ValueTask.FromException<UserInfo?>(new IOException("Language lookup unavailable")));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = range
            ? await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -3 + rangeCount - 1, Guid.NewGuid())
            : await _service.VoluntellAsync(volunteerId, shift.Id, Guid.NewGuid());

        result.Success.Should().BeTrue();
        var args = Notifier.ReceivedCalls()
            .Where(call => call.GetArguments()[0] is NotificationSource source && source == NotificationSource.ShiftAssigned)
            .Single().GetArguments();
        ((IReadOnlyList<Guid>)args[4]!).Should().ContainSingle().Which.Should().Be(volunteerId);
        var culture = CultureInfo.GetCultureInfo(lookupFailure ? "en" : "es");
        var resources = new ResourceManager(typeof(ShiftsResource));
        args[3].Should().Be(string.Format(culture,
            resources.GetString(range ? rangeCount == 1 ? "Shifts_Notification_AssignedRangeSingular" : "Shifts_Notification_AssignedRange"
                : "Shifts_Notification_AssignedDay", culture)!,
            rota.Name, range ? rangeCount : shift.DayOffset));
        args[7].Should().Be(resources.GetString("Shifts_BrowseAvailable", culture));
    }

    [HumansFact]
    public async Task Voluntell_FutureShift_SendsAssignedNotification()
    {
        // Default scenario seeds shifts around gate opening 2026-07-01 → future vs TestNow (2026-06-15).
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

        result.Success.Should().BeTrue();
        await AssertShiftAssignedSent(Notifier, 1);
    }

    [HumansFact]
    public async Task Voluntell_PastShift_SuppressesAssignedNotification_ButStillAuditsAndConfirms()
    {
        // Make the shift end in the past, mirroring MarkNoShow_AfterShiftEnd:
        // gate opening 2026-06-14, day 0, 08:00 +2h → ends 10:00 Madrid = 08:00 UTC < TestNow (12:00 UTC).
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        es.GateOpeningDate = new LocalDate(2026, 6, 14);
        shift.DayOffset = 0;
        shift.StartTime = new LocalTime(8, 0);
        shift.Duration = Duration.FromHours(2);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);

        // Volunteer-facing ShiftAssigned must NOT be sent for a past shift...
        await AssertShiftAssignedNotSent(Notifier);

        // ...but the audit entry is still written.
        await AuditLog.Received().LogAsync(
            AuditAction.ShiftSignupVoluntold, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), enrollerId,
            Arg.Any<Guid?>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task Voluntell_ShiftAtMaxVolunteers_ReturnsError()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        for (var i = 0; i < shift.MaxVolunteers; i++)
            SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellAsync(Guid.NewGuid(), shift.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_AtCapacity");
    }

    [HumansFact]
    public async Task Voluntell_OverlappingConfirmedSignup_ReturnsError()
    {
        var (_, rota, shift1) = SeedShiftScenario(SignupPolicy.Public);
        var shift2 = SeedShift(rota, dayOffset: 1, startHour: 10, durationHours: 4);
        var volunteerId = Guid.NewGuid();
        SeedSignup(volunteerId, shift1.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellAsync(volunteerId, shift2.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Time conflict");
        (await ShiftsDb.ShiftSignups.CountAsync(s => s.UserId == volunteerId, TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    // ============================================================
    // MarkNoShow
    // ============================================================

    [HumansFact]
    public async Task MarkNoShow_BeforeShiftEnd_ReturnsError()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
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
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.MarkNoShowAsync(signup.Id, reviewerId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("before the shift ends");
    }

    [HumansTheory(Timeout = 10000)]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    public async Task MarkNoShow_AfterShiftEnd_SetsNoShow(int dayOffset, bool affectsEarlyEntry)
    {
        var (es, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Both the event-day and build-day shift finish before TestNow.
        es.GateOpeningDate = new LocalDate(2026, 6, 14);
        rota.Period = affectsEarlyEntry ? RotaPeriod.Build : RotaPeriod.Event;
        shift.DayOffset = dayOffset;
        shift.StartTime = new LocalTime(8, 0);
        shift.Duration = Duration.FromHours(2);

        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        var reviewerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.MarkNoShowAsync(signup.Id, reviewerId);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.NoShow);
    }

    [HumansTheory]
    [InlineData(SignupStatus.NoShow)]
    [InlineData(SignupStatus.Bailed)]
    [InlineData(SignupStatus.Cancelled)]
    [InlineData(SignupStatus.Pending)]
    [InlineData(SignupStatus.Refused)]
    public async Task MarkNoShow_NotConfirmed_ReturnsErrorInsteadOfThrowing(SignupStatus status)
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        // Same past-ending shift as MarkNoShow_AfterShiftEnd_SetsNoShow, so the
        // shift-end guard passes and the status guard is the one under test.
        es.GateOpeningDate = new LocalDate(2026, 6, 14);
        shift.DayOffset = 0;
        shift.StartTime = new LocalTime(8, 0);
        shift.Duration = Duration.FromHours(2);

        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, status);
        var reviewerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.MarkNoShowAsync(signup.Id, reviewerId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(status.ToString());
    }

    // ============================================================
    // SignUpRange
    // ============================================================

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignUpRange_OverlappingSelectedShifts_RejectsOrSkipsTheConflictingDay(bool skipConflicts)
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        SeedAllDayShift(rota, -3);
        SeedAllDayShift(rota, -2);
        SeedAllDayShift(rota, -2);
        SeedAllDayShift(rota, -1);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1,
            flags: skipConflicts ? ShiftSignupRequestFlags.SkipConflicts : ShiftSignupRequestFlags.None);

        var signups = await ShiftsDb.ShiftSignups.AsNoTracking()
            .Where(s => s.UserId == userId).Include(s => s.Shift)
            .ToListAsync(TestContext.Current.CancellationToken);
        if (skipConflicts)
        {
            result.Success.Should().BeTrue();
            result.Warning.Should().Contain("Time conflict");
            signups.Select(s => s.Shift.DayOffset).Order().Should().Equal(-3, -1);
        }
        else
        {
            result.Success.Should().BeFalse();
            result.Error.Should().Contain("Time conflict");
            signups.Should().BeEmpty();
        }
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApproveRange_OverlappingCommitments_ConfirmsWithWarning(bool overlapsWithinBlock)
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        for (var day = -3; day <= -1; day++)
            SeedSignup(userId, SeedAllDayShift(rota, day).Id, SignupStatus.Pending).SignupBlockId = blockId;
        if (overlapsWithinBlock)
            SeedSignup(userId, SeedAllDayShift(rota, -2).Id, SignupStatus.Pending).SignupBlockId = blockId;
        else
        {
            var (_, otherRota, _) = SeedShiftScenario(SignupPolicy.Public);
            SeedSignup(userId, SeedAllDayShift(otherRota, -2).Id, SignupStatus.Confirmed);
        }
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveRangeAsync(blockId, Guid.NewGuid());

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("Time conflict");
        var block = await ShiftsDb.ShiftSignups.AsNoTracking()
            .Where(s => s.SignupBlockId == blockId).ToListAsync(TestContext.Current.CancellationToken);
        block.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Confirmed));
    }

    [HumansFact]
    public async Task VoluntellRange_OverlappingSelectedShifts_SkipsTheConflictingDay()
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        SeedAllDayShift(rota, -3);
        SeedAllDayShift(rota, -2);
        SeedAllDayShift(rota, -2);
        SeedAllDayShift(rota, -1);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellRangeAsync(userId, rota.Id, -3, -1, Guid.NewGuid());

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("time conflicts");
        var signups = await ShiftsDb.ShiftSignups.AsNoTracking()
            .Where(s => s.UserId == userId).Include(s => s.Shift)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Select(s => s.Shift.DayOffset).Order().Should().Equal(-3, -1);
        signups.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Confirmed));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignUpRange_CreatesOneSignupPerDay(bool mergedAlias)
    {
        // Arrange: rota with 5 all-day shifts (days -5 to -1), user picks days -3 to -1
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        // Add 5 all-day shifts
        for (var day = -5; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        var inputId = mergedAlias ? Guid.NewGuid() : userId;
        _users.GetUserInfoAsync(inputId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(userId) with { State = UserState.Active });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(inputId, rota.Id, -3, -1);

        // Assert: 3 signups created, all share same SignupBlockId
        result.Success.Should().BeTrue();
        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Should().HaveCount(3);
        signups.Should().AllSatisfy(s => s.SignupBlockId.Should().NotBeNull());
        signups.Select(s => s.SignupBlockId).Distinct().Should().HaveCount(1);
    }

    [HumansTheory]
    [InlineData(SignupStatus.Confirmed)]
    [InlineData(SignupStatus.Pending)]
    public async Task SignUpRange_BlocksIfAnyDayOverlaps(SignupStatus status)
    {
        // Arrange: user already has an active signup on day -2
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -5; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        // Find the day -2 shift and create an existing signup
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var dayMinus2Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2, TestContext.Current.CancellationToken);
        SeedSignup(userId, dayMinus2Shift.Id, status);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act: try to sign up for days -3 to -1
        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1);

        // Assert: fails — duplicate signup check catches this before overlap check
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Already signed up");
    }

    [HumansTheory]
    [InlineData(SignupStatus.Confirmed)]
    [InlineData(SignupStatus.Pending)]
    public async Task SignUpRange_SkipConflicts_FiltersAlreadySignedUpDays(SignupStatus status)
    {
        // Arrange: rota with 3 all-day shifts; user already signed up to day -2 in same rota.
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var dayMinus2Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2, TestContext.Current.CancellationToken);
        var existingSignup = SeedSignup(userId, dayMinus2Shift.Id, status);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(
            userId,
            rota.Id,
            -3,
            -1,
            flags: ShiftSignupRequestFlags.SkipConflicts);

        // Assert
        result.Success.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("Already signed up");

        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Should().HaveCount(3); // 1 pre-existing + 2 new
        var newOffsets = signups
            .Where(s => s.Id != existingSignup.Id)
            .Join(ShiftsDb.Shifts, s => s.ShiftId, sh => sh.Id, (_, sh) => sh.DayOffset)
            .OrderBy(o => o)
            .ToList();
        newOffsets.Should().Equal(-3, -1);
    }

    [HumansFact]
    public async Task SignUpRange_SkipConflicts_PreservesBothSkipAndCapacityWarnings()
    {
        // Arrange: rota with 4 all-day shifts (days -4 to -1).
        // User is already signed up to day -3 (skipConflicts case).
        // Day -2 is at capacity from other users (capacity-warning case).
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        for (var day = -4; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var dayMinus3Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -3, TestContext.Current.CancellationToken);
        SeedSignup(userId, dayMinus3Shift.Id, SignupStatus.Confirmed);

        var dayMinus2Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2, TestContext.Current.CancellationToken);
        // SeedAllDayShift sets MaxVolunteers = 5; fill day -2 with 5 distinct other users.
        for (var i = 0; i < dayMinus2Shift.MaxVolunteers; i++)
            SeedSignup(Guid.NewGuid(), dayMinus2Shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(
            userId,
            rota.Id,
            -4,
            -1,
            flags: ShiftSignupRequestFlags.SkipConflicts);

        // Assert: both warnings preserved, exactly 2 new signups (offsets -4 and -1).
        result.Success.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("Already signed up");
        result.Warning.Should().Contain("at capacity");

        var newSignups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId && s.SignupBlockId != null)
            .Join(ShiftsDb.Shifts, s => s.ShiftId, sh => sh.Id, (_, sh) => sh.DayOffset)
            .OrderBy(o => o)
            .ToListAsync(TestContext.Current.CancellationToken);
        newSignups.Should().Equal(-4, -1);
    }

    [HumansFact]
    public async Task SignUpRange_SkipConflicts_FiltersTimeOverlappingDays()
    {
        // Arrange: Build rota + a separate Event rota with a 12:00-14:00 shift on day -2.
        // All-day Build/Strike window is 08:00-18:00 (Shift.AllDayWindowStart/End).
        var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
        buildRota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(buildRota, day);

        var otherRota = new Rota
        {
            Id = Guid.NewGuid(),
            Name = "Kitchen",
            EventSettingsId = es.Id,
            Period = RotaPeriod.Event,
            Policy = SignupPolicy.Public,
            TeamId = buildRota.TeamId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        otherRota.EventSettings = es; // nav property for in-memory provider
        ShiftsDb.Rotas.Add(otherRota);

        var conflictingShift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = otherRota.Id,
            DayOffset = -2,
            StartTime = new LocalTime(12, 0),
            Duration = Duration.FromHours(2),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            IsAllDay = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        conflictingShift.Rota = otherRota; // nav property for in-memory provider
        ShiftsDb.Shifts.Add(conflictingShift);

        var userId = Guid.NewGuid();
        SeedSignup(userId, conflictingShift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(
            userId,
            buildRota.Id,
            -3,
            -1,
            flags: ShiftSignupRequestFlags.SkipConflicts);

        // Assert
        result.Success.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("Time conflict");

        var newOffsets = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId && s.Shift.RotaId == buildRota.Id)
            .Join(ShiftsDb.Shifts, s => s.ShiftId, sh => sh.Id, (_, sh) => sh.DayOffset)
            .OrderBy(o => o)
            .ToListAsync(TestContext.Current.CancellationToken);
        newOffsets.Should().Equal(-3, -1);
    }

    [HumansFact]
    public async Task SignUpRange_SkipConflicts_AllDaysConflict_ReturnsFailWithSummary()
    {
        // Arrange: rota with 3 all-day shifts, user already signed up to ALL three.
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        var userId = Guid.NewGuid();
        var shifts = new List<Shift>();
        for (var day = -3; day <= -1; day++)
            shifts.Add(SeedAllDayShift(rota, day));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        foreach (var shift in shifts)
            SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(
            userId,
            rota.Id,
            -3,
            -1,
            flags: ShiftSignupRequestFlags.SkipConflicts);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("Nothing to add");

        var totalSignups = await ShiftsDb.ShiftSignups.CountAsync(s => s.UserId == userId, TestContext.Current.CancellationToken);
        totalSignups.Should().Be(3); // only the 3 pre-existing
    }

    [HumansFact]
    public async Task SignUpRange_SkipConflicts_MixedKinds_AddsFreeDaysWithBothWarnings()
    {
        // Arrange: range -4..-1.
        //   day -4: free
        //   day -3: already signed up to this same Build rota
        //   day -2: cross-rota 12:00-14:00 conflict
        //   day -1: free
        var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
        buildRota.Period = RotaPeriod.Build;
        for (var day = -4; day <= -1; day++)
            SeedAllDayShift(buildRota, day);

        var otherRota = new Rota
        {
            Id = Guid.NewGuid(),
            Name = "Kitchen",
            EventSettingsId = es.Id,
            Period = RotaPeriod.Event,
            Policy = SignupPolicy.Public,
            TeamId = buildRota.TeamId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        otherRota.EventSettings = es;
        ShiftsDb.Rotas.Add(otherRota);

        var crossRotaShift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = otherRota.Id,
            DayOffset = -2,
            StartTime = new LocalTime(12, 0),
            Duration = Duration.FromHours(2),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            IsAllDay = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        crossRotaShift.Rota = otherRota;
        ShiftsDb.Shifts.Add(crossRotaShift);

        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var dayMinus3Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == buildRota.Id && s.DayOffset == -3, TestContext.Current.CancellationToken);
        SeedSignup(userId, dayMinus3Shift.Id, SignupStatus.Confirmed);  // already-signed-up case
        SeedSignup(userId, crossRotaShift.Id, SignupStatus.Confirmed);  // time-conflict case
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.SignUpRangeAsync(
            userId,
            buildRota.Id,
            -4,
            -1,
            flags: ShiftSignupRequestFlags.SkipConflicts);

        // Assert: -4 and -1 added; both warning kinds present
        result.Success.Should().BeTrue();
        result.Warning.Should().NotBeNull();
        result.Warning.Should().Contain("Already signed up");
        result.Warning.Should().Contain("Time conflict");

        var newOffsets = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId && s.Shift.RotaId == buildRota.Id && s.ShiftId != dayMinus3Shift.Id)
            .Join(ShiftsDb.Shifts, s => s.ShiftId, sh => sh.Id, (_, sh) => sh.DayOffset)
            .OrderBy(o => o)
            .ToListAsync(TestContext.Current.CancellationToken);
        newOffsets.Should().Equal(-4, -1);
    }

    [HumansTheory]
    [InlineData(SignupStatus.Confirmed, false)]
    [InlineData(SignupStatus.Pending, false)]
    [InlineData(SignupStatus.Pending, true)]
    public async Task SignUpRange_TimeOverlap_OnlyConfirmedSignupsConflict(SignupStatus status, bool skipConflicts)
    {
        // Arrange: cross-rota time conflict on day -2 (same shape as the skipConflicts time-overlap test).
        var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
        buildRota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(buildRota, day);

        var otherRota = new Rota
        {
            Id = Guid.NewGuid(),
            Name = "Kitchen",
            EventSettingsId = es.Id,
            Period = RotaPeriod.Event,
            Policy = SignupPolicy.Public,
            TeamId = buildRota.TeamId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        otherRota.EventSettings = es;
        ShiftsDb.Rotas.Add(otherRota);

        var conflictingShift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = otherRota.Id,
            DayOffset = -2,
            StartTime = new LocalTime(12, 0),
            Duration = Duration.FromHours(2),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            IsAllDay = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        conflictingShift.Rota = otherRota;
        ShiftsDb.Shifts.Add(conflictingShift);

        var userId = Guid.NewGuid();
        SeedSignup(userId, conflictingShift.Id, status);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(userId, buildRota.Id, -3, -1,
            flags: skipConflicts ? ShiftSignupRequestFlags.SkipConflicts : ShiftSignupRequestFlags.None);

        if (status == SignupStatus.Confirmed)
        {
            result.Success.Should().BeFalse();
            result.Error.Should().Contain("Time conflict");
        }
        else
        {
            result.Success.Should().BeTrue();
            result.Warning.Should().NotContain("Time conflict");
        }

        var newSignupCount = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == userId && s.Shift.RotaId == buildRota.Id)
            .CountAsync(TestContext.Current.CancellationToken);
        newSignupCount.Should().Be(status == SignupStatus.Confirmed ? 0 : 3);
    }

    [HumansFact]
    public async Task SignUpRange_BuildRotaAfterEeClose_NonPrivileged_ReturnsError()
    {
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        rota.Period = RotaPeriod.Build;
        SeedAllDayShift(rota, dayOffset: -3);
        SeedAllDayShift(rota, dayOffset: -2);
        es.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -2);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Shifts_Signup_EarlyEntryClosed");
    }

    // ============================================================
    // BailRange
    // ============================================================

    [HumansTheory]
    [InlineData(false, false, "Shifts_Bail_NotAuthorized")]
    [InlineData(true, false, "Shifts_BailRange_NotAuthorized")]
    [InlineData(false, true, "Shifts_Bail_EarlyEntryClosed")]
    [InlineData(true, true, "Shifts_Bail_EarlyEntryClosed")]
    public async Task Bail_RejectionsAreLocalizedWithoutMutatingSignups(
        bool range, bool earlyEntryClosed, string key)
    {
        var (settings, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.DayOffset = -1;
        if (earlyEntryClosed) settings.EarlyEntryClose = TestNow - Duration.FromHours(1);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        signup.SignupBlockId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _localizer[key].Returns(new LocalizedString(key, "translated rejection"));
        var actor = earlyEntryClosed ? userId : Guid.NewGuid();

        if (range)
        {
            var result = await _service.BailRangeAsync(signup.SignupBlockId.Value, actor);
            result.ErrorKey.Should().Be(key);
        }
        else
        {
            var result = await _service.BailAsync(signup.Id, actor, null);
            result.Success.Should().BeFalse();
            result.Error.Should().Be("translated rejection");
        }
        (await ShiftsDb.ShiftSignups.FindAsync([signup.Id], TestContext.Current.CancellationToken))!
            .Status.Should().Be(SignupStatus.Confirmed);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Notifier.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task BailRange_MissingCalendarReturnsAKeyWithoutMutatingSignups()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        signup.SignupBlockId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var settings = Substitute.For<Humans.Settings.Contracts.ISettingsService>();
        var serviceProvider = new ServiceLocatorBuilder()
            .With(_teamService).With(_teamService)
            .With(_roleAssignmentService).With(_users).Build();
        var service = new ShiftSignupService(
            _repo, Substitute.For<IVolunteerTrackingRepository>(), _shiftMgmt, new EventCalendarResolver(settings),
            AuditLog, Notifier, AdminAuthorization, _viewInvalidator,
            serviceProvider, Clock, NullLogger<ShiftSignupService>.Instance, _users, _localizer);

        var result = await service.BailRangeAsync(signup.SignupBlockId.Value, userId);

        result.ErrorKey.Should().Be("Shifts_EventCalendarNotConfigured");
        signup.Status.Should().Be(SignupStatus.Confirmed);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Notifier.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task BailRange_BailsAllSignupsInBlock()
    {
        // Arrange: user signed up for days -3 to -1 (shared SignupBlockId)
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.Public);
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
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        await _service.BailRangeAsync(blockId, userId);

        // Assert: all 3 signups now Bailed
        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.SignupBlockId == blockId)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Should().HaveCount(3);
        signups.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Bailed));
    }

    // ============================================================
    // ApproveRange / RefuseRange
    // ============================================================

    [HumansFact]
    public async Task ApproveRange_ConfirmsAllPendingSignupsInBlock()
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var shifts = new List<Shift> { SeedAllDayShift(rota, 1), SeedAllDayShift(rota, 2) };
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        foreach (var shift in shifts)
        {
            var signup = SeedSignup(userId, shift.Id, SignupStatus.Pending);
            signup.SignupBlockId = blockId;
        }
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveRangeAsync(blockId, Guid.NewGuid());

        result.Success.Should().BeTrue();
        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.SignupBlockId == blockId)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Should().HaveCount(2);
        signups.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Confirmed));
    }

    [HumansFact]
    public async Task ApproveRange_AutoRefusesPendingOnShiftFilledSinceRequest()
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var filledShift = SeedAllDayShift(rota, 1);
        var freeShift = SeedAllDayShift(rota, 2);
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        for (var i = 0; i < filledShift.MaxVolunteers; i++)
            SeedSignup(Guid.NewGuid(), filledShift.Id, SignupStatus.Confirmed);
        var filledPending = SeedSignup(userId, filledShift.Id, SignupStatus.Pending);
        filledPending.SignupBlockId = blockId;
        var freePending = SeedSignup(userId, freeShift.Id, SignupStatus.Pending);
        freePending.SignupBlockId = blockId;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveRangeAsync(blockId, Guid.NewGuid());

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("capacity");
        (await ShiftsDb.ShiftSignups.SingleAsync(s => s.Id == freePending.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(SignupStatus.Confirmed);
        (await ShiftsDb.ShiftSignups.SingleAsync(s => s.Id == filledPending.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(SignupStatus.Refused);
    }

    [HumansFact]
    public async Task RefuseRangeAsync_RefusesAllPendingSignupsInBlock()
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var shifts = new List<Shift> { SeedAllDayShift(rota, 1), SeedAllDayShift(rota, 2) };
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        foreach (var shift in shifts)
        {
            var signup = SeedSignup(userId, shift.Id, SignupStatus.Pending);
            signup.SignupBlockId = blockId;
        }
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RefuseRangeAsync(blockId, Guid.NewGuid(), "no longer needed");

        result.Success.Should().BeTrue();
        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.SignupBlockId == blockId)
            .ToListAsync(TestContext.Current.CancellationToken);
        signups.Should().HaveCount(2);
        signups.Should().AllSatisfy(s => s.Status.Should().Be(SignupStatus.Refused));
    }

    // ============================================================
    // VoluntellRange
    // ============================================================

    [HumansFact]
    public async Task VoluntellRange_CreatesConfirmedSignupsAcrossDateRange()
    {
        // Arrange: rota with 3 all-day shifts (days -3 to -1)
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        _users.GetUserInfoAsync(volunteerId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubHelpers.MakeUserInfo(volunteerId) with { State = UserState.AdminSuspended });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        // Assert
        result.Success.Should().BeTrue();
        var signups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == volunteerId)
            .ToListAsync(TestContext.Current.CancellationToken);
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

    [HumansFact]
    public async Task VoluntellRange_SkipsShiftsWhereUserAlreadySignedUp()
    {
        // Arrange: rota with 3 all-day shifts, user already signed up on day -2
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Pre-existing signup on day -2
        var dayMinus2Shift = await ShiftsDb.Shifts
            .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2, TestContext.Current.CancellationToken);
        SeedSignup(volunteerId, dayMinus2Shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        // Assert: only 2 new signups (day -3 and day -1), skipping day -2
        result.Success.Should().BeTrue();
        var newSignups = await ShiftsDb.ShiftSignups
            .Where(s => s.UserId == volunteerId && s.Enrolled)
            .ToListAsync(TestContext.Current.CancellationToken);
        newSignups.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task VoluntellRange_ReturnsError_WhenRotaNotFound()
    {
        const string key = "Shifts_Signup_RotaNotFound";
        _localizer[key].Returns(new LocalizedString(key, "translated missing rota"));
        // Act
        var result = await _service.VoluntellRangeAsync(Guid.NewGuid(), Guid.NewGuid(), -3, -1, Guid.NewGuid());

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("translated missing rota");
    }

    [HumansFact]
    public async Task VoluntellRange_AllPast_SuppressesAssignedNotification()
    {
        // gate opening 2026-06-14 → build days -3..-1 = 2026-06-11..13, all-day windows end
        // 18:00 Madrid = 16:00 UTC, all before TestNow (2026-06-15 12:00 UTC) → all past.
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        es.GateOpeningDate = new LocalDate(2026, 6, 14);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        result.Success.Should().BeTrue();
        await AssertShiftAssignedNotSent(Notifier);
    }

    [HumansFact]
    public async Task VoluntellRange_SpansPastAndFuture_SendsAssignedNotification()
    {
        // gate opening 2026-06-16 → days -3..-1 = 2026-06-13,14,15. Day -1 (2026-06-15) all-day
        // window ends 18:00 Madrid = 16:00 UTC > TestNow (12:00 UTC) → future present in range.
        var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
        es.GateOpeningDate = new LocalDate(2026, 6, 16);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var volunteerId = Guid.NewGuid();
        var enrollerId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

        result.Success.Should().BeTrue();
        await AssertShiftAssignedSent(Notifier, 1);
    }

    // ============================================================
    // Audit completeness — ShiftSignupCreated
    // ============================================================

    [HumansFact]
    public async Task SignUp_PublicPolicy_WritesShiftSignupCreatedAudit()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        await AuditLog.Received(1).LogAsync(
            AuditAction.ShiftSignupCreated, nameof(ShiftSignup), Saved(result).Id,
            Arg.Is<string>(s => s.Contains("(confirmed)")),
            userId,
            userId, nameof(User));
    }

    [HumansFact]
    public async Task SignUp_RequireApprovalPolicy_WritesShiftSignupCreatedAudit()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Pending);
        await AuditLog.Received(1).LogAsync(
            AuditAction.ShiftSignupCreated, nameof(ShiftSignup), Saved(result).Id,
            Arg.Is<string>(s => s.Contains("(pending)")),
            userId,
            userId, nameof(User));
    }

    [HumansFact]
    public async Task SignUp_WritesEnglishAuditDate_RegardlessOfUiCulture()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        SignupResult result;
        using (new CultureScope("es"))
            result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        await AuditLog.Received(1).LogAsync(
            AuditAction.ShiftSignupCreated, nameof(ShiftSignup), Saved(result).Id,
            Arg.Is<string>(s => s.Contains("on Thu Jul 2 (confirmed)")),
            userId,
            userId, nameof(User));
    }

    [HumansFact]
    public async Task SignUpRange_RequireApproval_WritesShiftSignupCreatedPerSignup()
    {
        var (_, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
        rota.Period = RotaPeriod.Build;
        for (var day = -3; day <= -1; day++)
            SeedAllDayShift(rota, day);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1);

        result.Success.Should().BeTrue();
        await AuditLog.Received(3).LogAsync(
            AuditAction.ShiftSignupCreated, nameof(ShiftSignup), Arg.Any<Guid>(),
            Arg.Is<string>(s => s.Contains("(range, pending)")),
            userId,
            userId, nameof(User));
    }

    // ============================================================
    // ShiftSignupChange notification completeness
    // ============================================================

    [HumansFact]
    public async Task SignUp_PublicPolicy_DispatchesShiftSignupChangeNotification()
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(BuildTeamInfoWithCoordinator(rota.TeamId, coordinatorId));
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        await AssertShiftSignupChangeSent(coordinatorId);
    }

    [HumansFact]
    public async Task Approve_DispatchesShiftSignupChangeNotification()
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(BuildTeamInfoWithCoordinator(rota.TeamId, coordinatorId));
        var signup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Pending);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApproveAsync(signup.Id, Guid.NewGuid());

        result.Success.Should().BeTrue();
        await AssertShiftSignupChangeSent(coordinatorId);
    }

    [HumansFact]
    public async Task Refuse_DispatchesShiftSignupChangeNotification()
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(BuildTeamInfoWithCoordinator(rota.TeamId, coordinatorId));
        var signup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Pending);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RefuseAsync(signup.Id, Guid.NewGuid(), "not needed");

        result.Success.Should().BeTrue();
        await AssertShiftSignupChangeSent(coordinatorId);
    }

    [HumansFact]
    public async Task Bail_DispatchesShiftSignupChangeNotification()
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(BuildTeamInfoWithCoordinator(rota.TeamId, coordinatorId));
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.BailAsync(signup.Id, userId, null);

        result.Success.Should().BeTrue();
        await AssertShiftSignupChangeSent(coordinatorId);
    }

    [HumansFact]
    public async Task Remove_DispatchesShiftSignupChangeNotification()
    {
        var (_, rota, shift) = SeedShiftScenario(SignupPolicy.Public);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(BuildTeamInfoWithCoordinator(rota.TeamId, coordinatorId));
        var signup = SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RemoveSignupAsync(signup.Id, Guid.NewGuid(), null);

        result.Success.Should().BeTrue();
        await AssertShiftSignupChangeSent(coordinatorId);
    }

    private async Task AssertShiftSignupChangeSent(Guid coordinatorId) =>
        await Notifier.Received(1).SendAsync(
            NotificationSource.ShiftSignupChange,
            Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(c => c.Contains(coordinatorId)),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

    private static TeamInfo BuildTeamInfoWithCoordinator(Guid teamId, Guid coordinatorUserId) =>
        new(
            teamId, "Team", null, "team",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members:
            [
                new(Guid.NewGuid(), coordinatorUserId, string.Empty, null, null, TeamMemberRole.Coordinator,
                    Instant.MinValue)
            ]);

    // ============================================================
    // Cache eviction on signup mutations
    // ============================================================

    [HumansFact]
    public async Task SignUp_EvictsUserAndShiftViewCache()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(userId, shift.Id);

        result.Success.Should().BeTrue();
        _viewInvalidator.Received(1).InvalidateUser(userId);
        _viewInvalidator.Received(1).InvalidateShift(shift.Id);
    }

    [HumansFact]
    public async Task SignUp_EvictsDashboardCache_SoNewlyFilledShiftShowsImmediately()
    {
        // Min=2, Max=5 (SeedShiftScenario default). One Confirmed signup exists — below min.
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        // Prime the dashboard cache while the shift is still short of MinVolunteers.
        var before = await _shiftMgmt.GetDashboardOverviewAsync(es.Id);
        before.FilledShifts.Should().Be(0);

        var result = await _service.SignUpAsync(Guid.NewGuid(), shift.Id);
        result.Success.Should().BeTrue();

        // If the cache weren't invalidated, this would still read the primed 0.
        var after = await _shiftMgmt.GetDashboardOverviewAsync(es.Id);
        after.FilledShifts.Should().Be(1);
    }

    [HumansFact]
    public async Task Bail_EvictsUserAndShiftViewCache()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.BailAsync(signup.Id, userId, null);

        result.Success.Should().BeTrue();
        _viewInvalidator.Received(1).InvalidateUser(userId);
        _viewInvalidator.Received(1).InvalidateShift(shift.Id);
    }

    [HumansFact]
    public async Task Bail_EvictsDashboardCache_SoDroppedBelowMinShowsImmediately()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var userId = Guid.NewGuid();
        var signup = SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
        SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed); // 2 of Min=2 — filled
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var before = await _shiftMgmt.GetDashboardOverviewAsync(es.Id);
        before.FilledShifts.Should().Be(1);

        var result = await _service.BailAsync(signup.Id, userId, null);
        result.Success.Should().BeTrue();

        var after = await _shiftMgmt.GetDashboardOverviewAsync(es.Id);
        after.FilledShifts.Should().Be(0);
    }

    // ============================================================
    // Account-merge fold — ShiftSignupReassigned
    // ============================================================

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignupLifecycleMutation_RefreshesWarmDashboard(bool merging)
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        SeedSignup(source, shift.Id, SignupStatus.Confirmed);
        if (merging) SeedSignup(target, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        (await _shiftMgmt.GetDashboardOverviewAsync(es.Id)).FilledSlots.Should().Be(merging ? 2 : 1);

        if (merging)
            await _service.ReassignAsync(source, target, Guid.NewGuid(), TestNow, TestContext.Current.CancellationToken);
        else
            await _service.CancelActiveSignupsForUserAsync(source, "Account deletion", TestContext.Current.CancellationToken);

        (await _shiftMgmt.GetDashboardOverviewAsync(es.Id)).FilledSlots.Should().Be(merging ? 1 : 0);
    }

    [HumansFact]
    public async Task BulkSignupDeletion_InvalidatesAffectedEventDashboards()
    {
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var user = Guid.NewGuid();
        SeedSignup(user, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var repo = Substitute.For<IShiftManagementRepository>();
        repo.GetForUsersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), null, Arg.Any<CancellationToken>())
            .Returns(c => _repo.GetForUsersAsync(c.Arg<IReadOnlyCollection<Guid>>(), ct: c.Arg<CancellationToken>()));
        repo.DeleteAllForUsersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        var management = Substitute.For<IShiftManagement>();
        var service = new ShiftSignupService(repo, Substitute.For<IVolunteerTrackingRepository>(), management,
            NewCalendarResolver(), AuditLog, Notifier, AdminAuthorization, _viewInvalidator,
            new ServiceLocatorBuilder().Build(),
            Clock, NullLogger<ShiftSignupService>.Instance, _users, _localizer);

        await service.DeleteAllForUsersAsync([user], TestContext.Current.CancellationToken);

        management.Received(1).InvalidateDashboardCaches(es.Id);
    }

    [HumansFact]
    public async Task Reassign_WhenSourceHasSignups_WritesShiftSignupReassignedOnce()
    {
        var (_, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        SeedSignup(sourceUserId, shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.ReassignAsync(sourceUserId, targetUserId, actorUserId, TestNow, TestContext.Current.CancellationToken);

        await AuditLog.Received(1).LogAsync(
            AuditAction.ShiftSignupReassigned, nameof(User), targetUserId,
            Arg.Is<string>(s => s.Contains("Reassigned 1")),
            actorUserId,
            targetUserId, nameof(User));
    }

    [HumansFact]
    public async Task Reassign_WhenSourceHasNoSignups_DoesNotWriteAudit()
    {
        SeedShiftScenario(SignupPolicy.Public);
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.ReassignAsync(sourceUserId, targetUserId, actorUserId, TestNow, TestContext.Current.CancellationToken);

        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.ShiftSignupReassigned, Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [InlineData("en", false)]
    [InlineData("es", false)]
    [InlineData("de", false)]
    [InlineData("it", false)]
    [InlineData("fr", false)]
    [InlineData("ca", false)]
    [InlineData("en", true)]
    [InlineData("es", true)]
    [InlineData("de", true)]
    [InlineData("it", true)]
    [InlineData("fr", true)]
    [InlineData("ca", true)]
    public async Task SignUp_EarlyEntryCapacityWarning_IsLocalizedAndStillConfirms(string cultureName, bool filledCapacity)
    {
        using var culture = new CultureScope(cultureName);
        LocalizeSignupMessages(cultureName);
        var (es, _, shift) = SeedShiftScenario(SignupPolicy.Public);
        shift.DayOffset = -1;
        es.EarlyEntryCapacity = new Dictionary<int, int> { [-1] = filledCapacity ? 1 : 0 };
        if (filledCapacity)
            SeedSignup(Guid.NewGuid(), shift.Id, SignupStatus.Confirmed);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.SignUpAsync(Guid.NewGuid(), shift.Id);

        result.Success.Should().BeTrue();
        Saved(result).Status.Should().Be(SignupStatus.Confirmed);
        var resources = new ResourceManager(typeof(ShiftsResource));
        result.Warning.Should().Be(string.Format(CultureInfo.CurrentUICulture,
            resources.GetString("Shifts_Signup_RangeEarlyEntryCapacityDays", CultureInfo.CurrentUICulture)!,
            es.GateOpeningDate.PlusDays(-1).ToWeekdayDayMonth()));
    }

    private void LocalizeSignupMessages(string cultureName)
    {
        var resources = new ResourceManager(typeof(ShiftsResource));
        var culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (var key in new[] { "Shifts_Signup_TimeConflict", "Shifts_Signup_UnknownTeam", "Shifts_Signup_RangeEarlyEntryCapacityDays" })
        {
            _localizer[key].Returns(new LocalizedString(key, resources.GetString(key, culture)!));
            _localizer[key, Arg.Any<object[]>()].Returns(call => new LocalizedString(key,
                string.Format(culture, resources.GetString(key, culture)!, call.Arg<object[]>())));
        }
    }

    [HumansFact]
    public async Task GetByUserAsync_ResolvesEachSignupAgainstItsOwnEvent()
    {
        // Same DayOffset + StartTime in two events in different time zones: the
        // instants can only differ if each row is resolved against its own calendar.
        var (_, madridRota, madridShift) = SeedShiftScenario(SignupPolicy.Public);
        var (aucklandEs, aucklandRota, _) = SeedShiftScenario(SignupPolicy.Public);
        aucklandEs.IsActive = false;
        aucklandEs.TimeZoneId = "Pacific/Auckland";
        aucklandEs.GateOpeningDate = new LocalDate(2025, 1, 15);
        aucklandRota.Name = "Southern Rota";
        var aucklandShift = SeedShift(aucklandRota, dayOffset: 1, startHour: 10, durationHours: 4);
        var userId = Guid.NewGuid();
        SeedSignup(userId, madridShift.Id, SignupStatus.Confirmed);
        SeedSignup(userId, aucklandShift.Id, SignupStatus.Pending);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var rows = await _service.GetByUserAsync(userId, ct: TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        var madrid = rows.Single(r => r.ShiftId == madridShift.Id);
        madrid.RotaName.Should().Be(madridRota.Name);
        madrid.Status.Should().Be(SignupStatus.Confirmed);
        // 2026-07-02 10:00 Europe/Madrid = 08:00Z
        madrid.AbsoluteStart.Should().Be(Instant.FromUtc(2026, 7, 2, 8, 0));
        var auckland = rows.Single(r => r.ShiftId == aucklandShift.Id);
        auckland.RotaName.Should().Be("Southern Rota");
        auckland.EventSettingsId.Should().Be(aucklandEs.Id);
        // 2025-01-16 10:00 Pacific/Auckland = 2025-01-15 21:00Z
        auckland.AbsoluteStart.Should().Be(Instant.FromUtc(2025, 1, 15, 21, 0));
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
            Priority = priority,
            Policy = policy,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        ShiftsDb.Rotas.Add(rota);

        // Set navigation properties for in-memory provider
        rota.EventSettings = es;

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
        ShiftsDb.Shifts.Add(shift);
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
            // StartTime/Duration are don't-care for IsAllDay rows; store midnight/24h sentinel.
            StartTime = LocalTime.Midnight,
            Duration = Duration.FromHours(24),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        shift.Rota = rota;
        ShiftsDb.Shifts.Add(shift);
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
        ShiftsDb.ShiftSignups.Add(signup);
        return signup;
    }

    private static async Task AssertShiftAssignedSent(INotificationEmitter n, int times) =>
        await n.Received(times).SendAsync(
            NotificationSource.ShiftAssigned,
            Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

    private static async Task AssertShiftAssignedNotSent(INotificationEmitter n) =>
        await n.DidNotReceive().SendAsync(
            NotificationSource.ShiftAssigned,
            Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

    /// <summary>
    /// The persisted signup row a <see cref="SignupResult"/> refers to.
    /// The result carries the id rather than the entity — the row is the
    /// section's own and does not cross the boundary
    /// (nobodies-collective/Humans#866).
    /// </summary>
    private ShiftSignup Saved(SignupResult result) =>
        ShiftsDb.ShiftSignups.Single(s => s.Id == result.SignupId!.Value);

}

using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Repositories.Shifts;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class ShiftSignupServiceCalendarFeedTests : ServiceTestHarness
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly ShiftSignupService _service;
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    public ShiftSignupServiceCalendarFeedTests()
        : base(TestNow)
    {
        var serviceProvider = new ServiceLocatorBuilder()
            .With(_teamService)
            .Build();

        var repo = new ShiftRepository(DbFactory, Db, Clock);
        var shiftMgmt = new ShiftManagementService(
            repo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            Clock);

        _service = new ShiftSignupService(
            repo,
            Substitute.For<IVolunteerTrackingRepository>(),
            shiftMgmt,
            Substitute.For<IBurnSettingsService>(),
            AuditLog,
            Substitute.For<INotificationEmitter>(),
            AdminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IEarlyEntryInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    private (EventSettings es, Rota rota, Shift shift) SeedShiftScenario()
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
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            PracticalInfo = "Meet at the gate.",
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        rota.EventSettings = es;
        Db.Rotas.Add(rota);

        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 1,
            StartTime = new LocalTime(10, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        shift.Rota = rota;
        Db.Shifts.Add(shift);
        return (es, rota, shift);
    }

    private ShiftSignup SeedSignup(Shift shift, Guid userId, SignupStatus status)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        signup.Shift = shift;
        Db.ShiftSignups.Add(signup);
        return signup;
    }

    [HumansFact]
    public async Task GetCalendarItems_ConfirmedSignup_ResolvesAbsoluteTimesAndTeamName()
    {
        var (es, rota, shift) = SeedShiftScenario();
        var userId = Guid.NewGuid();
        var signup = SeedSignup(shift, userId, SignupStatus.Confirmed);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>
            {
                [rota.TeamId] = new(
                    Id: rota.TeamId,
                    Name: "Test Department",
                    Description: null,
                    Slug: "test-dept",
                    IsActive: true,
                    IsSystemTeam: false,
                    SystemTeamType: SystemTeamType.None,
                    RequiresApproval: false,
                    IsPublicPage: false,
                    IsHidden: false,
                    IsPromotedToDirectory: false,
                    CreatedAt: TestNow,
                    Members: [])
            });
        await Db.SaveChangesAsync();

        var items = await _service.GetCalendarItemsForUserAsync(userId, CancellationToken.None);

        items.Should().HaveCount(1);
        var item = items[0];
        item.Uid.Should().Be($"shift-{signup.Id}@humans.nobodies.team");
        item.Source.Should().Be("Shifts");
        item.Summary.Should().Be("Test Department: Test Rota");
        // 2026-07-02 (gate +1) 10:00 Europe/Madrid = 08:00 UTC (CEST).
        item.Start.Should().Be(Instant.FromUtc(2026, 7, 2, 8, 0));
        item.End.Should().Be(Instant.FromUtc(2026, 7, 2, 12, 0));
        item.Description.Should().Contain("Meet at the gate.");
        item.Url.Should().Be("https://humans.nobodies.team/Shifts/Mine");
    }

    [HumansFact]
    public async Task GetCalendarItems_PendingSignup_MarkedPendingInSummary()
    {
        var (_, _, shift) = SeedShiftScenario();
        var userId = Guid.NewGuid();
        SeedSignup(shift, userId, SignupStatus.Pending);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        await Db.SaveChangesAsync();

        var items = await _service.GetCalendarItemsForUserAsync(userId, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Summary.Should().Be("Test Rota (pending)");
    }

    [HumansTheory]
    [InlineData(SignupStatus.Cancelled)]
    [InlineData(SignupStatus.Bailed)]
    [InlineData(SignupStatus.NoShow)]
    [InlineData(SignupStatus.Refused)]
    public async Task GetCalendarItems_InactiveStatuses_Excluded(SignupStatus status)
    {
        var (_, _, shift) = SeedShiftScenario();
        var userId = Guid.NewGuid();
        SeedSignup(shift, userId, status);
        await Db.SaveChangesAsync();

        var items = await _service.GetCalendarItemsForUserAsync(userId, CancellationToken.None);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCalendarItems_NoSignups_ReturnsEmpty()
    {
        var items = await _service.GetCalendarItemsForUserAsync(Guid.NewGuid(), CancellationToken.None);

        items.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCalendarItems_NoDescriptionOrPracticalInfo_DescriptionIsNull()
    {
        var (_, rota, shift) = SeedShiftScenario();
        rota.PracticalInfo = null;
        shift.Description = null;
        var userId = Guid.NewGuid();
        SeedSignup(shift, userId, SignupStatus.Confirmed);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        await Db.SaveChangesAsync();

        var items = await _service.GetCalendarItemsForUserAsync(userId, CancellationToken.None);

        items.Should().HaveCount(1);
        items[0].Description.Should().BeNull();
    }

    [HumansFact]
    public async Task GetCalendarItems_AllDayShift_UsesAllDayWindow()
    {
        var (_, _, shift) = SeedShiftScenario();
        shift.IsAllDay = true;
        var userId = Guid.NewGuid();
        SeedSignup(shift, userId, SignupStatus.Confirmed);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        await Db.SaveChangesAsync();

        var items = await _service.GetCalendarItemsForUserAsync(userId, CancellationToken.None);

        items.Should().HaveCount(1);
        // All-day window is 08:00–18:00 Europe/Madrid (Shift.AllDayWindowStart/End);
        // 2026-07-02 is CEST (UTC+2) → 06:00–16:00 UTC.
        items[0].Start.Should().Be(Instant.FromUtc(2026, 7, 2, 6, 0));
        items[0].End.Should().Be(Instant.FromUtc(2026, 7, 2, 16, 0));
    }
}

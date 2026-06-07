using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Unit tests for <see cref="ShiftRepository"/>'s Shift Summary aggregation read
/// (<c>GetConfirmedUserShiftTotalsAsync</c>): confirmed-only filtering, hours =
/// Σ duration, count = # signups, and the global / team-set / single-rota scopes.
/// </summary>
public class ShiftRepositorySummaryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ShiftRepository _repo;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    // Shared scenario ids.
    private readonly Guid _event = Guid.NewGuid();
    private readonly Guid _otherEvent = Guid.NewGuid();
    private readonly Guid _team1 = Guid.NewGuid();
    private readonly Guid _team2 = Guid.NewGuid();
    private readonly Guid _rota1 = Guid.NewGuid();
    private readonly Guid _rota2 = Guid.NewGuid();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly Guid _userC = Guid.NewGuid();

    public ShiftRepositorySummaryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ShiftRepository(new TestDbContextFactory(options), _dbContext, new FakeClock(TestNow));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_GlobalScope_SumsHoursAndCountsConfirmedOnly()
    {
        SeedScenario();

        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event);

        var byUser = totals.ToDictionary(t => t.UserId);
        // userA: rota1 S1 (4h) + rota1 S2 (2h) = 6h over 2 confirmed signups.
        byUser[_userA].Hours.Should().Be(6.0);
        byUser[_userA].Count.Should().Be(2);
        // userB: rota2 S3 (8h) = 8h over 1 confirmed signup.
        byUser[_userB].Hours.Should().Be(8.0);
        byUser[_userB].Count.Should().Be(1);
        // userC only has a Pending signup → excluded entirely.
        byUser.ContainsKey(_userC).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_TeamScope_RestrictsToTeamSet()
    {
        SeedScenario();

        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event, teamIds: [_team1]);

        var byUser = totals.ToDictionary(t => t.UserId);
        byUser.Keys.Should().BeEquivalentTo([_userA]);
        byUser[_userA].Hours.Should().Be(6.0);
        byUser[_userA].Count.Should().Be(2);
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_RotaScope_RestrictsToSingleRota()
    {
        SeedScenario();

        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event, rotaId: _rota2);

        var byUser = totals.ToDictionary(t => t.UserId);
        byUser.Keys.Should().BeEquivalentTo([_userB]);
        byUser[_userB].Hours.Should().Be(8.0);
        byUser[_userB].Count.Should().Be(1);
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_OtherEventConfirmedSignups_AreExcluded()
    {
        SeedScenario();

        // userA also has a confirmed signup in a different event — must not leak in.
        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event);

        totals.Single(t => t.UserId == _userA).Hours.Should().Be(6.0);
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_AllDayShift_UsesWindowHoursNotStoredDuration()
    {
        SeedEvent(_event);
        SeedTeam(_team1);
        SeedRota(_rota1, _event, _team1);
        // Build/strike all-day rows store a 24h sentinel Duration, but the effective
        // worked hours are the 08:00–18:00 window (10h) — see Shift.IsAllDay.
        var allDay = SeedShift(_rota1, Duration.FromHours(24), isAllDay: true);
        _dbContext.ShiftSignups.Add(MakeSignup(_userA, allDay, SignupStatus.Confirmed));
        await _dbContext.SaveChangesAsync();

        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event);

        totals.Single(t => t.UserId == _userA).Hours.Should().Be(Shift.AllDayWindowHours);
    }

    [HumansFact]
    public async Task GetConfirmedUserShiftTotalsAsync_EmptyEvent_ReturnsEmpty()
    {
        SeedEvent(_event);
        await _dbContext.SaveChangesAsync();

        var totals = await _repo.GetConfirmedUserShiftTotalsAsync(_event);

        totals.Should().BeEmpty();
    }

    // ── seed ────────────────────────────────────────────────────────────────

    private void SeedScenario()
    {
        SeedEvent(_event);
        SeedEvent(_otherEvent);
        SeedTeam(_team1);
        SeedTeam(_team2);
        SeedRota(_rota1, _event, _team1);
        SeedRota(_rota2, _event, _team2);

        var s1 = SeedShift(_rota1, Duration.FromHours(4));
        var s2 = SeedShift(_rota1, Duration.FromHours(2));
        var s3 = SeedShift(_rota2, Duration.FromHours(8));

        // Other-event rota + shift for the leakage check.
        var otherTeam = Guid.NewGuid();
        var otherRota = Guid.NewGuid();
        SeedTeam(otherTeam);
        SeedRota(otherRota, _otherEvent, otherTeam);
        var sOther = SeedShift(otherRota, Duration.FromHours(99));

        _dbContext.ShiftSignups.AddRange(
            MakeSignup(_userA, s1, SignupStatus.Confirmed),
            MakeSignup(_userA, s2, SignupStatus.Confirmed),
            MakeSignup(_userB, s3, SignupStatus.Confirmed),
            MakeSignup(_userC, s1, SignupStatus.Pending),
            MakeSignup(_userA, sOther, SignupStatus.Confirmed));

        _dbContext.SaveChanges();
    }

    private void SeedEvent(Guid esId) => _dbContext.EventSettings.Add(new EventSettings
    {
        Id = esId,
        EventName = "TestEvent",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        TimeZoneId = "UTC"
    });

    private void SeedTeam(Guid teamId) => _dbContext.Teams.Add(new Team
    {
        Id = teamId,
        Name = "Team-" + teamId.ToString()[..8],
        Slug = "team-" + teamId.ToString()[..8],
        IsActive = true
    });

    private void SeedRota(Guid rotaId, Guid esId, Guid teamId) => _dbContext.Rotas.Add(new Rota
    {
        Id = rotaId,
        Name = "Rota-" + rotaId.ToString()[..8],
        TeamId = teamId,
        EventSettingsId = esId,
        Policy = SignupPolicy.Public,
        Period = RotaPeriod.Event
    });

    private Guid SeedShift(Guid rotaId, Duration duration, bool isAllDay = false)
    {
        var shiftId = Guid.NewGuid();
        _dbContext.Shifts.Add(new Shift
        {
            Id = shiftId,
            RotaId = rotaId,
            DayOffset = 0,
            StartTime = new LocalTime(8, 0),
            Duration = duration,
            IsAllDay = isAllDay,
            MaxVolunteers = 10
        });
        return shiftId;
    }

    private static ShiftSignup MakeSignup(Guid userId, Guid shiftId, SignupStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shiftId,
        Status = status,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };
}

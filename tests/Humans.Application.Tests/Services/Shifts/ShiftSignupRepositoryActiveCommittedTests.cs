using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using NodaTime;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Covers the narrow read used by Mailer audience computations:
/// "users with at least one Pending or Confirmed signup for the given event".
/// </summary>
public sealed class ShiftRepositoryActiveCommittedTests : ServiceTestHarness
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly ShiftRepository _repo;

    public ShiftRepositoryActiveCommittedTests()
        : base(TestNow)
    {
        _repo = new ShiftRepository(DbFactory, Db, Clock);
    }

    [HumansFact]
    public async Task GetActiveCommittedUserIdsForEventAsync_IncludesPendingAndConfirmed_ExcludesOthers()
    {
        var (es, _, shift) = await SeedShiftAsync();
        var pending = Guid.NewGuid();
        var confirmed = Guid.NewGuid();
        var refused = Guid.NewGuid();
        var bailed = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        var noShow = Guid.NewGuid();

        await AddSignupAsync(shift, pending, SignupStatus.Pending);
        await AddSignupAsync(shift, confirmed, SignupStatus.Confirmed);
        await AddSignupAsync(shift, refused, SignupStatus.Refused);
        await AddSignupAsync(shift, bailed, SignupStatus.Bailed);
        await AddSignupAsync(shift, cancelled, SignupStatus.Cancelled);
        await AddSignupAsync(shift, noShow, SignupStatus.NoShow);

        var result = await _repo.GetActiveCommittedUserIdsForEventAsync(es.Id, CancellationToken.None);

        result.Should().BeEquivalentTo([pending, confirmed]);
    }

    [HumansFact]
    public async Task GetActiveCommittedUserIdsForEventAsync_FiltersByEvent()
    {
        var (esA, _, shiftA) = await SeedShiftAsync(eventName: "Event A");
        var (esB, _, shiftB) = await SeedShiftAsync(eventName: "Event B");
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await AddSignupAsync(shiftA, userA, SignupStatus.Confirmed);
        await AddSignupAsync(shiftB, userB, SignupStatus.Confirmed);

        var resultA = await _repo.GetActiveCommittedUserIdsForEventAsync(esA.Id, CancellationToken.None);

        resultA.Should().BeEquivalentTo([userA]);
        resultA.Should().NotContain(userB);
    }

    [HumansFact]
    public async Task GetActiveCommittedUserIdsForEventAsync_DistinctUsersOnly()
    {
        var (es, _, shift) = await SeedShiftAsync();
        var userA = Guid.NewGuid();
        // Same user with two signups on the same shift (synthetic).
        await AddSignupAsync(shift, userA, SignupStatus.Pending);
        await AddSignupAsync(shift, userA, SignupStatus.Confirmed);

        var result = await _repo.GetActiveCommittedUserIdsForEventAsync(es.Id, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(userA);
    }

    private async Task<(EventSettings es, Rota rota, Shift shift)> SeedShiftAsync(string eventName = "Test Event")
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = eventName,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.EventSettings.Add(es);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"Dept-{eventName}",
            Slug = $"dept-{eventName.ToLowerInvariant().Replace(' ', '-')}",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        Db.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = $"Rota-{eventName}",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es,
        };
        Db.Rotas.Add(rota);

        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 1,
            StartTime = new LocalTime(10, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 10,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return (es, rota, shift);
    }

    private async Task AddSignupAsync(Shift shift, Guid userId, SignupStatus status)
    {
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        });
        await Db.SaveChangesAsync();
    }
}

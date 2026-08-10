using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary>
/// Integration tests for <see cref="VolunteerTrackingRepository"/>. Takes the
/// assembly-shared <see cref="HumansWebApplicationFactory"/> through
/// <see cref="IntegrationTestBase"/>, resolves the Scoped
/// <see cref="HumansDbContext"/> per test through a DI scope off
/// <see cref="IntegrationTestBase.Factory"/>, and exercises the repository
/// against the run's single PostgreSQL container.
/// </summary>
public class VolunteerTrackingRepositoryTests(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact]
    public async Task GetBuildStatusesForEventAsync_returns_empty_when_no_row_exists()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var sut = new VolunteerTrackingRepository(shiftsDb);
        var userId = Guid.NewGuid();

        var result = await sut.GetBuildStatusesForEventAsync(Guid.NewGuid(), [userId], TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UpsertCampSetupAsync_inserts_when_no_row_exists()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        var trimmed = await sut.UpsertCampSetupAsync(
            userId, es.Id,
            barrioSetupStartDate: new LocalDate(2026, 7, 1),
            notes: "left for barrio",
            setByUserId: Guid.NewGuid(),
            setAt: SystemClock.Instance.GetCurrentInstant(),
            setupOffsetThreshold: null, ct: TestContext.Current.CancellationToken);

        trimmed.Should().BeEmpty();

        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.UserId.Should().Be(userId);
        fetched.BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
        fetched.Notes.Should().Be("left for barrio");
    }

    [HumansFact]
    public async Task GetBuildStatusesForEventAsync_returns_only_rows_for_requested_event()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es1 = await SeedActiveEventAsync(shiftsDb);
        var es2 = await SeedActiveEventAsync(shiftsDb);
        var sut = new VolunteerTrackingRepository(shiftsDb);

        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();

        // Two rows on es1, one on es2.
        await sut.UpsertCampSetupAsync(u1, es1.Id, new LocalDate(2026, 6, 30), null, null, null, null, TestContext.Current.CancellationToken);
        await sut.UpsertCampSetupAsync(u2, es1.Id, new LocalDate(2026, 7, 1), null, null, null, null, TestContext.Current.CancellationToken);
        await sut.UpsertCampSetupAsync(u3, es2.Id, new LocalDate(2026, 6, 25), null, null, null, null, TestContext.Current.CancellationToken);

        var rows = await sut.GetBuildStatusesForEventAsync(es1.Id, ct: TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        rows.Select(r => r.UserId).Should().BeEquivalentTo([u1, u2]);
        rows.Should().OnlyContain(r => r.EventSettingsId == es1.Id);
    }

    [HumansFact]
    public async Task GetEligibleBuildSignupsAsync_returns_only_build_period_active_signups_in_event()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);   // BuildStartOffset = -10
        var sut = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var teamId = (await SeedTeamAsync(db)).Id;
        var userId = (await SeedUserAsync(db)).Id;

        // Build-period rota with a shift at -7 — shift exists but no signup,
        // so this should NOT appear in the result.
        var buildRotaA = SeedRota(shiftsDb, es.Id, teamId, RotaPeriod.Build);
        SeedShift(shiftsDb, buildRotaA.Id, dayOffset: -7);

        // Event-period rota with a shift at +1 and a Confirmed signup.
        // Period is Event → NOT eligible.
        var eventRota = SeedRota(shiftsDb, es.Id, teamId, RotaPeriod.Event);
        var shiftEvent = SeedShift(shiftsDb, eventRota.Id, dayOffset: 1);
        SeedSignup(shiftsDb, userId, shiftEvent.Id, SignupStatus.Confirmed);

        // Build-period rota with a shift at -3 (Confirmed signup) and another
        // at -2 (Bailed signup). Only the -3 Confirmed should be returned.
        var buildRotaB = SeedRota(shiftsDb, es.Id, teamId, RotaPeriod.Build, name: "BuildB");
        var shiftBuildBNeg3 = SeedShift(shiftsDb, buildRotaB.Id, dayOffset: -3);
        var shiftBuildBNeg2 = SeedShift(shiftsDb, buildRotaB.Id, dayOffset: -2);
        SeedSignup(shiftsDb, userId, shiftBuildBNeg3.Id, SignupStatus.Confirmed);
        SeedSignup(shiftsDb, userId, shiftBuildBNeg2.Id, SignupStatus.Bailed);

        await shiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await sut.GetEligibleBuildSignupsAsync(es.Id, TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].DayOffset.Should().Be(-3);
        result[0].Status.Should().Be(SignupStatus.Confirmed);
        result[0].RotaName.Should().Be("BuildB");
    }

    // ---------------------------------------------------------------------
    // Day-off jsonb round-trips. These exercise the NodaTime-aware converter
    // configured on VolunteerBuildStatusConfiguration; without it,
    // DayOffEntry.MarkedAt would deserialize as Instant.MinValue and these
    // assertions would fail.
    // ---------------------------------------------------------------------

    [HumansFact(Timeout = 30000)]
    public async Task UpsertDayOffAsync_inserts_first_entry_and_creates_row_if_absent()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var markedAt = SystemClock.Instance.GetCurrentInstant();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        await sut.UpsertDayOffAsync(
            userId, es.Id,
            new DayOffEntry(DayOffset: -5, Reason: "doctor", MarkedByUserId: actor, MarkedAt: markedAt), TestContext.Current.CancellationToken);

        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.DayOffs.Should().HaveCount(1);
        fetched.DayOffs[0].DayOffset.Should().Be(-5);
        fetched.DayOffs[0].Reason.Should().Be("doctor");
        fetched.DayOffs[0].MarkedByUserId.Should().Be(actor);
        fetched.DayOffs[0].MarkedAt.Should().Be(markedAt);
    }

    [HumansFact(Timeout = 30000)]
    public async Task UpsertDayOffAsync_replaces_entry_for_same_day_offset()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var t1 = SystemClock.Instance.GetCurrentInstant();
        var t2 = t1 + Duration.FromMinutes(5);
        var sut = new VolunteerTrackingRepository(shiftsDb);

        await sut.UpsertDayOffAsync(
            userId, es.Id,
            new DayOffEntry(-5, "doctor", actor, t1), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(
            userId, es.Id,
            new DayOffEntry(-5, "family emergency", actor, t2), TestContext.Current.CancellationToken);

        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.DayOffs.Should().HaveCount(1);
        fetched.DayOffs[0].DayOffset.Should().Be(-5);
        fetched.DayOffs[0].Reason.Should().Be("family emergency");
        fetched.DayOffs[0].MarkedAt.Should().Be(t2);
    }

    [HumansFact(Timeout = 30000)]
    public async Task UpsertDayOffAsync_appends_entries_for_distinct_days_sorted_by_offset()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var t = SystemClock.Instance.GetCurrentInstant();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        // Insert out of order; persisted layout should sort ascending.
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-3, "a", actor, t), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-7, "b", actor, t), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-5, "c", actor, t), TestContext.Current.CancellationToken);

        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.DayOffs.Select(d => d.DayOffset).Should().Equal(-7, -5, -3);
    }

    [HumansFact(Timeout = 30000)]
    public async Task RemoveDayOffAsync_drops_only_the_specified_day()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var t = SystemClock.Instance.GetCurrentInstant();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-5, "a", actor, t), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-3, "b", actor, t), TestContext.Current.CancellationToken);

        var removed = await sut.RemoveDayOffAsync(userId, es.Id, -5, TestContext.Current.CancellationToken);

        removed.Should().BeTrue();
        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.DayOffs.Should().HaveCount(1);
        fetched.DayOffs[0].DayOffset.Should().Be(-3);
    }

    [HumansFact(Timeout = 30000)]
    public async Task RemoveDayOffAsync_returns_false_when_entry_absent()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var t = SystemClock.Instance.GetCurrentInstant();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        // Row does not exist at all.
        (await sut.RemoveDayOffAsync(userId, es.Id, -5, TestContext.Current.CancellationToken)).Should().BeFalse();

        // Row exists but no entry for that offset.
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-3, null, actor, t), TestContext.Current.CancellationToken);
        (await sut.RemoveDayOffAsync(userId, es.Id, -5, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact(Timeout = 30000)]
    public async Task UpsertCampSetupAsync_does_not_disturb_existing_DayOffs()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var es = await SeedActiveEventAsync(shiftsDb);
        var userId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var t = SystemClock.Instance.GetCurrentInstant();
        var sut = new VolunteerTrackingRepository(shiftsDb);

        // Seed three day-offs at offsets -8, -5, -3.
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-8, "a", actor, t), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-5, "b", actor, t), TestContext.Current.CancellationToken);
        await sut.UpsertDayOffAsync(userId, es.Id, new DayOffEntry(-3, "c", actor, t), TestContext.Current.CancellationToken);

        // Camp setup at offset -4 → trim threshold is -4 (auto-clear day-offs >= -4).
        // Only -3 should be auto-cleared; -8 and -5 stay.
        var trimmed = await sut.UpsertCampSetupAsync(
            userId, es.Id,
            barrioSetupStartDate: new LocalDate(2026, 6, 27),
            notes: null,
            setByUserId: actor,
            setAt: t,
            setupOffsetThreshold: -4, ct: TestContext.Current.CancellationToken);

        trimmed.Should().Equal(-3);
        var fetched = (await sut.GetBuildStatusesForEventAsync(es.Id, [userId], TestContext.Current.CancellationToken)).SingleOrDefault();
        fetched.Should().NotBeNull();
        fetched.DayOffs.Select(d => d.DayOffset).Should().Equal(-8, -5);
    }

    /// <summary>
    /// Seeds a fresh <see cref="EventSettings"/> row with a unique name so each
    /// test gets an isolated event id (the test container is shared across
    /// tests in the fixture).
    /// </summary>
    private static async Task<EventSettings> SeedActiveEventAsync(ShiftsDbContext shiftsDb)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = $"VTrack-{Guid.NewGuid():N}",
            Year = 2026,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -10,
            EventEndOffset = 6,
            StrikeEndOffset = 8,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shiftsDb.EventSettings.Add(es);
        await shiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        return es;
    }

    private static async Task<Team> SeedTeamAsync(HumansDbContext db)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"VTrack Team {Guid.NewGuid():N}",
            Slug = $"vtrack-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return team;
    }

    private static async Task<User> SeedUserAsync(HumansDbContext db)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"vtrack-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"VTRACK-{Guid.NewGuid():N}@EXAMPLE.TEST",
            DisplayName = "Volunteer Tracking Test User",
            CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private static Rota SeedRota(
        ShiftsDbContext shiftsDb, Guid eventSettingsId, Guid teamId,
        RotaPeriod period, string? name = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            TeamId = teamId,
            Name = name ?? $"Rota-{Guid.NewGuid():N}".Substring(0, 12),
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = period,
            IsVisibleToVolunteers = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shiftsDb.Rotas.Add(rota);
        return rota;
    }

    private static Shift SeedShift(
        ShiftsDbContext shiftsDb, Guid rotaId, int dayOffset)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rotaId,
            DayOffset = dayOffset,
            IsAllDay = true,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(10),
            MinVolunteers = 1,
            MaxVolunteers = 10,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shiftsDb.Shifts.Add(shift);
        return shift;
    }

    private static void SeedSignup(
        ShiftsDbContext shiftsDb, Guid userId, Guid shiftId, SignupStatus status)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shiftsDb.ShiftSignups.Add(signup);
    }
}

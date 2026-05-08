using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary>
/// Integration tests for <see cref="VolunteerTrackingRepository"/>. Mirrors the
/// repo's established service-test shape (e.g. <c>CalendarServiceTests</c>):
/// uses <see cref="IClassFixture{T}"/> for the test-container-backed factory,
/// resolves the Scoped <see cref="HumansDbContext"/> per test through a DI
/// scope, and exercises the repository against a real PostgreSQL container.
///
/// <see cref="IntegrationTestBase"/> is HttpClient-only, so it doesn't fit
/// repository tests; we use the factory directly per the
/// <c>CalendarServiceTests</c> pattern.
/// </summary>
public class VolunteerTrackingRepositoryTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public VolunteerTrackingRepositoryTests(HumansWebApplicationFactory factory) =>
        _factory = factory;

    [HumansFact]
    public async Task GetAsync_returns_null_when_no_row_exists()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var sut = new VolunteerTrackingRepository(db);

        var result = await sut.GetAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task UpsertCampSetupAsync_inserts_when_no_row_exists()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        var result = await sut.UpsertCampSetupAsync(
            userId, es.Id,
            barrioSetupStartDate: new LocalDate(2026, 7, 1),
            notes: "left for barrio",
            setByUserId: Guid.NewGuid(),
            setAt: SystemClock.Instance.GetCurrentInstant());

        result.UserId.Should().Be(userId);
        result.BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
        result.Notes.Should().Be("left for barrio");

        var fetched = await sut.GetAsync(userId, es.Id);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(result.Id);
    }

    [HumansFact]
    public async Task ReplaceBlockedDaysAsync_persists_sorted_deduped_list_and_returns_prior()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        // Seed an empty row first.
        await sut.UpsertCampSetupAsync(userId, es.Id, null, null, null, null);

        var prior = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3, -5, -3 });
        prior.Should().BeEmpty();

        var row = await sut.GetAsync(userId, es.Id);
        row.Should().NotBeNull();
        row!.BlockedDayOffsets.Should().Equal(-5, -3);   // sorted ascending, deduped

        var prior2 = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -7 });
        prior2.Should().Equal(-5, -3);
    }

    [HumansFact]
    public async Task UpsertCampSetupAsync_updates_existing_row_and_preserves_blocked_days()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 6, 30), "first", null, null);
        await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3 });
        await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 7, 1), "second", null, null);

        var rows = await db.VolunteerBuildStatuses
            .Where(x => x.UserId == userId && x.EventSettingsId == es.Id)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
        rows[0].Notes.Should().Be("second");
        rows[0].BlockedDayOffsets.Should().Equal(-3);   // preserved
    }

    [HumansFact]
    public async Task SetBlockAsync_add_when_absent_creates_row_and_returns_true()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        var changed = await sut.SetBlockAsync(userId, es.Id, -3, block: true);

        changed.Should().BeTrue();
        var row = await sut.GetAsync(userId, es.Id);
        row.Should().NotBeNull();
        row!.BlockedDayOffsets.Should().Equal(-3);
    }

    [HumansFact]
    public async Task SetBlockAsync_add_when_already_present_is_idempotent_and_returns_false()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        await sut.SetBlockAsync(userId, es.Id, -3, block: true);
        var changed = await sut.SetBlockAsync(userId, es.Id, -3, block: true);

        changed.Should().BeFalse();
        var row = await sut.GetAsync(userId, es.Id);
        row!.BlockedDayOffsets.Should().Equal(-3);   // still single entry
    }

    [HumansFact]
    public async Task SetBlockAsync_remove_when_present_returns_true_and_drops_offset()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -5, -3 });
        var changed = await sut.SetBlockAsync(userId, es.Id, -3, block: false);

        changed.Should().BeTrue();
        var row = await sut.GetAsync(userId, es.Id);
        row!.BlockedDayOffsets.Should().Equal(-5);
    }

    [HumansFact]
    public async Task SetBlockAsync_remove_when_absent_is_idempotent_and_returns_false()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        // No row exists at all — removing should be a no-op.
        var changed = await sut.SetBlockAsync(userId, es.Id, -3, block: false);

        changed.Should().BeFalse();
        var row = await sut.GetAsync(userId, es.Id);
        row.Should().BeNull();
    }

    [HumansFact]
    public async Task GetByEventAsync_returns_only_rows_for_requested_event()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es1 = await SeedActiveEventAsync(db);
        var es2 = await SeedActiveEventAsync(db);
        var sut = new VolunteerTrackingRepository(db);

        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();

        // Two rows on es1, one on es2.
        await sut.UpsertCampSetupAsync(u1, es1.Id, new LocalDate(2026, 6, 30), null, null, null);
        await sut.UpsertCampSetupAsync(u2, es1.Id, new LocalDate(2026, 7, 1), null, null, null);
        await sut.UpsertCampSetupAsync(u3, es2.Id, new LocalDate(2026, 6, 25), null, null, null);

        var rows = await sut.GetByEventAsync(es1.Id);

        rows.Should().HaveCount(2);
        rows.Select(r => r.UserId).Should().BeEquivalentTo(new[] { u1, u2 });
        rows.Should().OnlyContain(r => r.EventSettingsId == es1.Id);
    }

    [HumansFact]
    public async Task GetEligibleBuildSignupsAsync_returns_only_build_period_active_signups_in_event()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);   // BuildStartOffset = -10
        var sut = new VolunteerTrackingRepository(db);

        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Build-period rota with a shift at -7 — shift exists but no signup,
        // so this should NOT appear in the result.
        var buildRotaA = SeedRota(db, es.Id, teamId, RotaPeriod.Build);
        var shiftBuildAA = SeedShift(db, buildRotaA.Id, dayOffset: -7);

        // Event-period rota with a shift at +1 and a Confirmed signup.
        // Period is Event → NOT eligible.
        var eventRota = SeedRota(db, es.Id, teamId, RotaPeriod.Event);
        var shiftEvent = SeedShift(db, eventRota.Id, dayOffset: 1);
        SeedSignup(db, userId, shiftEvent.Id, SignupStatus.Confirmed);

        // Build-period rota with a shift at -3 (Confirmed signup) and another
        // at -2 (Bailed signup). Only the -3 Confirmed should be returned.
        var buildRotaB = SeedRota(db, es.Id, teamId, RotaPeriod.Build, name: "BuildB");
        var shiftBuildBNeg3 = SeedShift(db, buildRotaB.Id, dayOffset: -3);
        var shiftBuildBNeg2 = SeedShift(db, buildRotaB.Id, dayOffset: -2);
        SeedSignup(db, userId, shiftBuildBNeg3.Id, SignupStatus.Confirmed);
        SeedSignup(db, userId, shiftBuildBNeg2.Id, SignupStatus.Bailed);

        await db.SaveChangesAsync();

        var result = await sut.GetEligibleBuildSignupsAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userId);
        result[0].DayOffset.Should().Be(-3);
        result[0].Status.Should().Be(SignupStatus.Confirmed);
        result[0].RotaName.Should().Be("BuildB");
    }

    /// <summary>
    /// Seeds a fresh <see cref="EventSettings"/> row with a unique name so each
    /// test gets an isolated event id (the test container is shared across
    /// tests in the fixture).
    /// </summary>
    private static async Task<EventSettings> SeedActiveEventAsync(HumansDbContext db)
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
        db.EventSettings.Add(es);
        await db.SaveChangesAsync();
        return es;
    }

    private static Rota SeedRota(
        HumansDbContext db, Guid eventSettingsId, Guid teamId,
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
        db.Rotas.Add(rota);
        return rota;
    }

    private static Shift SeedShift(
        HumansDbContext db, Guid rotaId, int dayOffset)
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
        db.Shifts.Add(shift);
        return shift;
    }

    private static ShiftSignup SeedSignup(
        HumansDbContext db, Guid userId, Guid shiftId, SignupStatus status)
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
        db.ShiftSignups.Add(signup);
        return signup;
    }
}

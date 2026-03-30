using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Humans.Infrastructure.Data;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ShiftUrgencyTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ShiftManagementService _service;

    // Clock is March 1, 2026 12:00 UTC. Distant event settings (~122 days out)
    // keep proximity boost small so base score tests remain meaningful.
    private static readonly Instant TestNow = Instant.FromUtc(2026, 3, 1, 12, 0);
    private static readonly EventSettings DistantEvent = new()
    {
        GateOpeningDate = new LocalDate(2026, 7, 1),
        TimeZoneId = "UTC"
    };

    public ShiftUrgencyTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);

        _service = new ShiftManagementService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            new MemoryCache(new MemoryCacheOptions()),
            new FakeClock(TestNow),
            NullLogger<ShiftManagementService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CalculateScore_NormalPriority_5Remaining_4h_ReturnsExpected()
    {
        // Base: remaining=5, priority=Normal(1), duration=4h, understaffed=false(1) → 20
        // Proximity boost ≈ 1.08 (shift is ~122 days out) → ~21.6
        var shift = MakeShift(ShiftPriority.Normal, minVol: 2, maxVol: 7, durationHours: 4);
        var confirmedCount = 2;

        var score = _service.CalculateScore(shift, confirmedCount, DistantEvent);

        score.Should().BeApproximately(20 * 1.08, 0.5);
    }

    [Fact]
    public void CalculateScore_EssentialPriority_2Remaining_8h_Understaffed_ReturnsExpected()
    {
        // Base: remaining=2, priority=Essential(6), duration=8h, understaffed=true(2) → 192
        // Proximity boost ≈ 1.08 → ~207
        var shift = MakeShift(ShiftPriority.Essential, minVol: 5, maxVol: 6, durationHours: 8);
        var confirmedCount = 4;

        var score = _service.CalculateScore(shift, confirmedCount, DistantEvent);

        score.Should().BeApproximately(192 * 1.08, 5);
    }

    [Fact]
    public void CalculateScore_FullyStaffed_ReturnsZero()
    {
        var shift = MakeShift(ShiftPriority.Important, minVol: 2, maxVol: 5, durationHours: 4);
        var confirmedCount = 5;

        var score = _service.CalculateScore(shift, confirmedCount, DistantEvent);

        score.Should().Be(0);
    }

    [Fact]
    public void CalculateScore_ImminentShift_RanksHigherThanDistantWithMoreSlots()
    {
        // A shift tomorrow with 5 empty slots should outrank a shift 30 days out with 20 slots
        var tomorrowEvent = new EventSettings
        {
            GateOpeningDate = new LocalDate(2026, 3, 2),
            TimeZoneId = "UTC"
        };
        var distantEvent = new EventSettings
        {
            GateOpeningDate = new LocalDate(2026, 3, 31),
            TimeZoneId = "UTC"
        };

        var tomorrowShift = MakeShift(ShiftPriority.Normal, minVol: 2, maxVol: 7, durationHours: 8);
        var distantShift = MakeShift(ShiftPriority.Normal, minVol: 2, maxVol: 12, durationHours: 8);

        // Tomorrow: 5 remaining, ~1 day out → base=40, proximity ≈ 6x → ~240
        var tomorrowScore = _service.CalculateScore(tomorrowShift, 2, tomorrowEvent);
        // 30 days: 10 remaining, ~30 days out → base=80, proximity ≈ 1.3x → ~104
        var distantScore = _service.CalculateScore(distantShift, 2, distantEvent);

        tomorrowScore.Should().BeGreaterThan(distantScore);
    }

    private static Shift MakeShift(ShiftPriority priority, int minVol, int maxVol, double durationHours)
    {
        var rota = new Rota { Priority = priority };
        return new Shift
        {
            Id = Guid.NewGuid(),
            MinVolunteers = minVol,
            MaxVolunteers = maxVol,
            Duration = Duration.FromHours(durationHours),
            DayOffset = 0,
            StartTime = new LocalTime(8, 0),
            Rota = rota
        };
    }
}

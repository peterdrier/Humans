using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Humans.Infrastructure.Data;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ShiftUrgencyTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ShiftManagementService _service;

    public ShiftUrgencyTests()
    {
        // ShiftManagementService requires a DbContext even though CalculateScore is pure;
        // create a minimal one to satisfy the constructor.
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);

        _service = new ShiftManagementService(
            _dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0)),
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
        // remaining=5, priority=Normal(1), duration=4h, understaffed=false(1)
        // 5 * 1 * 4 * 1 = 20
        var shift = MakeShift(ShiftPriority.Normal, minVol: 2, maxVol: 7, durationHours: 4);
        var confirmedCount = 2; // remaining = 7 - 2 = 5, confirmed >= min so no understaffed

        var score = _service.CalculateScore(shift, confirmedCount);

        score.Should().Be(20);
    }

    [Fact]
    public void CalculateScore_EssentialPriority_2Remaining_8h_Understaffed_ReturnsExpected()
    {
        // remaining=2, priority=Essential(6), duration=8h, understaffed=true(2)
        // 2 * 6 * 8 * 2 = 192
        var shift = MakeShift(ShiftPriority.Essential, minVol: 5, maxVol: 6, durationHours: 8);
        var confirmedCount = 4; // remaining = 6 - 4 = 2, confirmed(4) < min(5) so understaffed

        var score = _service.CalculateScore(shift, confirmedCount);

        score.Should().Be(192);
    }

    [Fact]
    public void CalculateScore_FullyStaffed_ReturnsZero()
    {
        var shift = MakeShift(ShiftPriority.Important, minVol: 2, maxVol: 5, durationHours: 4);
        var confirmedCount = 5; // remaining = 5 - 5 = 0

        var score = _service.CalculateScore(shift, confirmedCount);

        score.Should().Be(0);
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
            Rota = rota
        };
    }
}

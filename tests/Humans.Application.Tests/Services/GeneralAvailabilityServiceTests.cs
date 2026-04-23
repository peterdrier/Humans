using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;
using GeneralAvailabilityService = Humans.Application.Services.Shifts.GeneralAvailabilityService;

namespace Humans.Application.Tests.Services;

public class GeneralAvailabilityServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly GeneralAvailabilityRepository _repo;
    private readonly GeneralAvailabilityService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public GeneralAvailabilityServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _repo = new GeneralAvailabilityRepository(new TestDbContextFactory(options));
        _service = new GeneralAvailabilityService(_repo, new FakeClock(TestNow));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SetAvailability_CreatesRecord()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await _dbContext.SaveChangesAsync();

        await _service.SetAvailabilityAsync(userId, esId, [-3, -2, -1]);

        var record = await _dbContext.GeneralAvailability
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        record.Should().NotBeNull();
        record!.AvailableDayOffsets.Should().BeEquivalentTo(new[] { -3, -2, -1 });
    }

    [Fact]
    public async Task SetAvailability_UpdatesExistingRecord()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await _dbContext.SaveChangesAsync();

        // First set
        await _service.SetAvailabilityAsync(userId, esId, [-3, -2]);

        // Update
        await _service.SetAvailabilityAsync(userId, esId, [0, 1, 2]);

        var records = await _dbContext.GeneralAvailability
            .AsNoTracking()
            .Where(g => g.UserId == userId && g.EventSettingsId == esId)
            .ToListAsync();
        records.Should().HaveCount(1);
        records[0].AvailableDayOffsets.Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task GetAvailableVolunteers_ReturnsMatchingDayOffset()
    {
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();
        var user3Id = Guid.NewGuid();
        var esId = SeedEventSettings();
        await _dbContext.SaveChangesAsync();

        await _service.SetAvailabilityAsync(user1Id, esId, [-3, -2, -1]);
        await _service.SetAvailabilityAsync(user2Id, esId, [-2, 0, 1]);
        await _service.SetAvailabilityAsync(user3Id, esId, [0, 1, 2]);

        // Query for day -2: should return user1 and user2
        var available = await _service.GetAvailableForDayAsync(esId, -2);
        available.Should().HaveCount(2);
        available.Select(a => a.UserId).Should().BeEquivalentTo(new[] { user1Id, user2Id });
    }

    [Fact]
    public async Task GetByUserAsync_ReturnsNullWhenMissing()
    {
        var result = await _service.GetByUserAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesExistingRecord()
    {
        var userId = Guid.NewGuid();
        var esId = SeedEventSettings();
        await _dbContext.SaveChangesAsync();

        await _service.SetAvailabilityAsync(userId, esId, [0, 1]);
        await _service.DeleteAsync(userId, esId);

        var after = await _dbContext.GeneralAvailability
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.EventSettingsId == esId);
        after.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NoOpWhenMissing()
    {
        // Should not throw when there's no matching record.
        await _service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid());
    }

    private Guid SeedEventSettings()
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.EventSettings.Add(es);
        return es.Id;
    }
}

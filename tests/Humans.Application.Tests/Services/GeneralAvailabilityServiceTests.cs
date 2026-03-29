using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GeneralAvailabilityServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly GeneralAvailabilityService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public GeneralAvailabilityServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _service = new GeneralAvailabilityService(_dbContext, new FakeClock(TestNow));
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

        // Seed users (required for Include)
        _dbContext.Users.Add(new User { Id = user1Id, DisplayName = "User1", UserName = "u1", Email = "u1@test.com" });
        _dbContext.Users.Add(new User { Id = user2Id, DisplayName = "User2", UserName = "u2", Email = "u2@test.com" });
        _dbContext.Users.Add(new User { Id = user3Id, DisplayName = "User3", UserName = "u3", Email = "u3@test.com" });
        await _dbContext.SaveChangesAsync();

        await _service.SetAvailabilityAsync(user1Id, esId, [-3, -2, -1]);
        await _service.SetAvailabilityAsync(user2Id, esId, [-2, 0, 1]);
        await _service.SetAvailabilityAsync(user3Id, esId, [0, 1, 2]);

        // Query for day -2: should return user1 and user2
        var available = await _service.GetAvailableForDayAsync(esId, -2);
        available.Should().HaveCount(2);
        available.Select(a => a.UserId).Should().BeEquivalentTo(new[] { user1Id, user2Id });
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

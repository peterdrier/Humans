using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services.Agent;
using Humans.Infrastructure.Stores;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentSettingsServiceTests
{
    [Fact]
    public async Task Updating_persists_to_db_and_refreshes_store()
    {
        await using var db = CreateDb();
        db.AgentSettings.Add(new AgentSettings
        {
            Id = 1, Enabled = false, Model = "claude-sonnet-4-6",
            PreloadConfig = AgentPreloadConfig.Tier1,
            DailyMessageCap = 30, HourlyMessageCap = 10, DailyTokenCap = 50000, RetentionDays = 90,
            UpdatedAt = Instant.FromUtc(2026, 4, 21, 0, 0)
        });
        await db.SaveChangesAsync();

        var store = new AgentSettingsStore();
        var service = new AgentSettingsService(db, store, new TestClock(Instant.FromUtc(2026, 4, 22, 9, 0)));
        await service.LoadAsync(CancellationToken.None);

        await service.UpdateAsync(s =>
        {
            s.Enabled = true;
            s.DailyMessageCap = 60;
        }, CancellationToken.None);

        store.Current.Enabled.Should().BeTrue();
        store.Current.DailyMessageCap.Should().Be(60);

        var reloaded = await db.AgentSettings.AsNoTracking().FirstAsync();
        reloaded.Enabled.Should().BeTrue();
        reloaded.DailyMessageCap.Should().Be(60);
    }

    private static HumansDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new HumansDbContext(options);
    }

    private sealed class TestClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }
}

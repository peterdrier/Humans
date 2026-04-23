using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using Xunit;
using SyncSettingsService = Humans.Application.Services.GoogleIntegration.SyncSettingsService;

namespace Humans.Application.Tests.Services;

public class SyncSettingsServiceTests : IDisposable
{
    private readonly HumansDbContext _seedContext;
    private readonly TestDbContextFactory _factory;
    private readonly FakeClock _clock;
    private readonly SyncSettingsService _service;

    public SyncSettingsServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _factory = new TestDbContextFactory(options);
        _seedContext = _factory.CreateDbContext();
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var repository = new SyncSettingsRepository(_factory);
        _service = new SyncSettingsService(repository, _clock);
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllSettings()
    {
        SeedSettings(3);
        await _seedContext.SaveChangesAsync();

        var result = await _service.GetAllAsync();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetModeAsync_ReturnsNone_ByDefault()
    {
        _seedContext.SyncServiceSettings.Add(new SyncServiceSettings
        {
            Id = Guid.NewGuid(),
            ServiceType = SyncServiceType.GoogleDrive,
            SyncMode = SyncMode.None,
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _seedContext.SaveChangesAsync();

        var result = await _service.GetModeAsync(SyncServiceType.GoogleDrive);

        result.Should().Be(SyncMode.None);
    }

    [Fact]
    public async Task UpdateModeAsync_ChangesModeAndTracksActor()
    {
        var actorId = Guid.NewGuid();
        _seedContext.SyncServiceSettings.Add(new SyncServiceSettings
        {
            Id = Guid.NewGuid(),
            ServiceType = SyncServiceType.GoogleGroups,
            SyncMode = SyncMode.None,
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        });
        await _seedContext.SaveChangesAsync();

        await _service.UpdateModeAsync(SyncServiceType.GoogleGroups, SyncMode.AddAndRemove, actorId);

        await using var verify = _factory.CreateDbContext();
        var updated = await verify.SyncServiceSettings
            .FirstAsync(s => s.ServiceType == SyncServiceType.GoogleGroups);
        updated.SyncMode.Should().Be(SyncMode.AddAndRemove);
        updated.UpdatedByUserId.Should().Be(actorId);
        updated.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task UpdateModeAsync_Throws_WhenServiceTypeHasNoRow()
    {
        var actorId = Guid.NewGuid();

        var act = () => _service.UpdateModeAsync(
            SyncServiceType.Discord, SyncMode.AddAndRemove, actorId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Discord*");
    }

    [Fact]
    public async Task GetModeAsync_ReturnsNone_WhenServiceTypeNotFound()
    {
        // No settings seeded at all
        var result = await _service.GetModeAsync(SyncServiceType.Discord);

        result.Should().Be(SyncMode.None);
    }

    private void SeedSettings(int count)
    {
        var serviceTypes = Enum.GetValues<SyncServiceType>();
        for (var i = 0; i < count && i < serviceTypes.Length; i++)
        {
            _seedContext.SyncServiceSettings.Add(new SyncServiceSettings
            {
                Id = Guid.NewGuid(),
                ServiceType = serviceTypes[i],
                SyncMode = SyncMode.None,
                UpdatedAt = _clock.GetCurrentInstant()
            });
        }
    }
}

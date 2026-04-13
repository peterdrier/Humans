using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Authorization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class SyncSettingsServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly SyncSettingsService _service;

    public SyncSettingsServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        // Default: authorize succeeds — specific denial tests override.
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        _service = new SyncSettingsService(
            _dbContext,
            _authorizationService,
            _clock,
            NullLogger<SyncSettingsService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllSettings()
    {
        SeedSettings(3);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetAllAsync();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetModeAsync_ReturnsNone_ByDefault()
    {
        _dbContext.SyncServiceSettings.Add(new SyncServiceSettings
        {
            Id = Guid.NewGuid(),
            ServiceType = SyncServiceType.GoogleDrive,
            SyncMode = SyncMode.None,
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetModeAsync(SyncServiceType.GoogleDrive);

        result.Should().Be(SyncMode.None);
    }

    [Fact]
    public async Task UpdateModeAsync_ChangesModeAndTracksActor()
    {
        var actorId = Guid.NewGuid();
        _dbContext.SyncServiceSettings.Add(new SyncServiceSettings
        {
            Id = Guid.NewGuid(),
            ServiceType = SyncServiceType.GoogleGroups,
            SyncMode = SyncMode.None,
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        });
        await _dbContext.SaveChangesAsync();

        await _service.UpdateModeAsync(
            SyncServiceType.GoogleGroups, SyncMode.AddAndRemove, actorId, SystemPrincipal.Instance);

        var updated = await _dbContext.SyncServiceSettings
            .FirstAsync(s => s.ServiceType == SyncServiceType.GoogleGroups);
        updated.SyncMode.Should().Be(SyncMode.AddAndRemove);
        updated.UpdatedByUserId.Should().Be(actorId);
        updated.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task UpdateModeAsync_ThrowsUnauthorized_WhenAuthorizationFails()
    {
        // Arrange: authorization denies.
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        _dbContext.SyncServiceSettings.Add(new SyncServiceSettings
        {
            Id = Guid.NewGuid(),
            ServiceType = SyncServiceType.GoogleGroups,
            SyncMode = SyncMode.None,
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0)
        });
        await _dbContext.SaveChangesAsync();

        var unprivileged = new ClaimsPrincipal(new ClaimsIdentity());

        // Act + Assert
        var act = async () => await _service.UpdateModeAsync(
            SyncServiceType.GoogleGroups, SyncMode.AddAndRemove, Guid.NewGuid(), unprivileged);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // The sync mode must NOT have been changed.
        var untouched = await _dbContext.SyncServiceSettings
            .FirstAsync(s => s.ServiceType == SyncServiceType.GoogleGroups);
        untouched.SyncMode.Should().Be(SyncMode.None);
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
            _dbContext.SyncServiceSettings.Add(new SyncServiceSettings
            {
                Id = Guid.NewGuid(),
                ServiceType = serviceTypes[i],
                SyncMode = SyncMode.None,
                UpdatedAt = _clock.GetCurrentInstant()
            });
        }
    }
}

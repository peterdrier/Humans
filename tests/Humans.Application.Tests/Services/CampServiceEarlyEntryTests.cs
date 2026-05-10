using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampServiceEarlyEntryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampService _service;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly InMemoryFileStorage _fileStorage;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly ICampRoleService _campRoleService;

    public CampServiceEarlyEntryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();
        _fileStorage = new InMemoryFileStorage();

        var factory = new TestDbContextFactory(options);
        var repo = new CampRepository(factory);
        var roleRepo = new CampRoleRepository(factory);

        // IUserService substitute — returns seeded users from the shared in-memory db.
        _userService = Substitute.For<IUserService>();
        _userService.GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                using var ctx = new HumansDbContext(options);
                var users = ctx.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToList();
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                    users.ToDictionary(u => u.Id));
            });

        _notificationEmitter = Substitute.For<INotificationEmitter>();

        _campRoleService = Substitute.For<ICampRoleService>();
        _campRoleService.RemoveAllForMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        _service = new CampService(
            repo,
            roleRepo,
            _userService,
            _auditLog,
            Substitute.For<ISystemTeamSync>(),
            _fileStorage,
            _notificationEmitter,
            Substitute.For<ICampLeadJoinRequestsBadgeCacheInvalidator>(),
            new Lazy<ICampRoleService>(() => _campRoleService),
            _clock,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<CampService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // SetEeStartDateAsync
    // ==========================================================================

    [HumansFact]
    public async Task SetEeStartDateAsync_SetsValue_AndInvalidatesSettingsCache()
    {
        await SeedSettingsAsync();
        var date = new LocalDate(2026, 8, 7);
        var actorUserId = Guid.NewGuid();

        await _service.SetEeStartDateAsync(date, actorUserId);

        var settings = await _service.GetSettingsAsync();
        settings.EeStartDate.Should().Be(date);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampSettingsEeStartDateChanged,
            nameof(CampSettings), Arg.Any<Guid>(),
            Arg.Any<string>(), actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task SeedSettingsAsync()
    {
        if (!await _dbContext.CampSettings.AnyAsync())
        {
            _dbContext.CampSettings.Add(new CampSettings
            {
                Id = Guid.Parse("00000000-0000-0000-0010-000000000001"),
                PublicYear = 2026,
                OpenSeasons = new List<int> { 2026 }
            });
            await _dbContext.SaveChangesAsync();
        }
    }
}

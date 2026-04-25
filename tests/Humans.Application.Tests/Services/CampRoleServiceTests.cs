using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Camps;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampRoleServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampRoleService _service;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly ICampService _campService;
    private readonly Guid _actorUserId = Guid.NewGuid();

    public CampRoleServiceTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(_options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 26, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();
        _userService = Substitute.For<IUserService>();
        _notificationEmitter = Substitute.For<INotificationEmitter>();
        _campService = Substitute.For<ICampService>();

        var factory = new TestDbContextFactory(_options);
        var repo = new CampRoleRepository(factory);

        _service = new CampRoleService(
            repo,
            _campService,
            _userService,
            _auditLog,
            _notificationEmitter,
            _clock,
            NullLogger<CampRoleService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ListDefinitions_excludes_deactivated_by_default()
    {
        await SeedDefinitionAsync("Active Role");
        await SeedDefinitionAsync("Old Role", deactivated: true);

        var result = await _service.ListDefinitionsAsync(includeDeactivated: false);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Active Role");
    }

    [HumansFact]
    public async Task ListDefinitions_includes_deactivated_when_requested()
    {
        await SeedDefinitionAsync("Active Role");
        await SeedDefinitionAsync("Old Role", deactivated: true);

        var result = await _service.ListDefinitionsAsync(includeDeactivated: true);

        result.Should().HaveCount(2);
    }

    private async Task<CampRoleDefinition> SeedDefinitionAsync(
        string name = "Consent Lead", int slotCount = 2, int minimumRequired = 1,
        bool isRequired = true, bool deactivated = false)
    {
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlotCount = slotCount,
            MinimumRequired = minimumRequired,
            IsRequired = isRequired,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
            DeactivatedAt = deactivated ? _clock.GetCurrentInstant() : null,
        };
        _dbContext.CampRoleDefinitions.Add(def);
        await _dbContext.SaveChangesAsync();
        return def;
    }

    private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(int year = 2026)
    {
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = $"camp-{Guid.NewGuid():N}".Substring(0, 12),
        };
        var season = new CampSeason
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            Year = year,
            Name = "Test Camp",
            Status = CampSeasonStatus.Active,
        };
        _dbContext.Camps.Add(camp);
        _dbContext.CampSeasons.Add(season);
        await _dbContext.SaveChangesAsync();
        return (camp, season);
    }

    private async Task<CampMember> SeedActiveMemberAsync(Guid seasonId, Guid? userId = null)
    {
        var member = new CampMember
        {
            Id = Guid.NewGuid(),
            CampSeasonId = seasonId,
            UserId = userId ?? Guid.NewGuid(),
            Status = CampMemberStatus.Active,
            RequestedAt = _clock.GetCurrentInstant(),
            ConfirmedAt = _clock.GetCurrentInstant(),
            ConfirmedByUserId = _actorUserId,
        };
        _dbContext.CampMembers.Add(member);
        await _dbContext.SaveChangesAsync();
        return member;
    }
}

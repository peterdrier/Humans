using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CampRoleTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CampService _service;
    private readonly IAuditLogService _auditLog;

    public CampRoleTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0));
        _auditLog = Substitute.For<IAuditLogService>();

        _service = new CampService(
            _dbContext,
            _auditLog,
            Substitute.For<ISystemTeamSync>(),
            _clock,
            new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // CreateCampRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task CreateCampRoleDefinitionAsync_PersistsAndReturnsDto()
    {
        var actorUserId = Guid.NewGuid();

        var dto = await _service.CreateCampRoleDefinitionAsync(
            name: "Test Role",
            description: "for tests",
            slotCount: 2,
            minimumRequired: 1,
            sortOrder: 100,
            isRequired: true,
            actorUserId: actorUserId);

        dto.Name.Should().Be("Test Role");
        dto.Description.Should().Be("for tests");
        dto.SlotCount.Should().Be(2);
        dto.MinimumRequired.Should().Be(1);
        dto.SortOrder.Should().Be(100);
        dto.IsRequired.Should().BeTrue();
        dto.DeactivatedAt.Should().BeNull();

        var row = await _dbContext.CampRoleDefinitions.FirstOrDefaultAsync(d => d.Id == dto.Id);
        row.Should().NotBeNull();
        row!.Name.Should().Be("Test Role");
        row.Description.Should().Be("for tests");
        row.SlotCount.Should().Be(2);
        row.MinimumRequired.Should().Be(1);
        row.SortOrder.Should().Be(100);
        row.IsRequired.Should().BeTrue();
        row.DeactivatedAt.Should().BeNull();
        row.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        row.UpdatedAt.Should().Be(_clock.GetCurrentInstant());

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionCreated,
            nameof(CampRoleDefinition),
            dto.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task CreateCampRoleDefinitionAsync_RejectsDuplicateName()
    {
        var actorUserId = Guid.NewGuid();
        await _service.CreateCampRoleDefinitionAsync(
            "Dup Role", null, 1, 0, 0, false, actorUserId);

        var act = () => _service.CreateCampRoleDefinitionAsync(
            "Dup Role", null, 1, 0, 0, false, actorUserId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    // ==========================================================================
    // UpdateCampRoleDefinitionAsync
    // ==========================================================================

    [Fact]
    public async Task UpdateCampRoleDefinitionAsync_UpdatesFieldsAndUpdatedAt()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateCampRoleDefinitionAsync(
            "Original", "old desc", 1, 0, 5, false, actorUserId);

        // Advance the clock so we can verify UpdatedAt changes
        var createdAt = _clock.GetCurrentInstant();
        _clock.AdvanceMinutes(30);
        var afterAdvance = _clock.GetCurrentInstant();

        var updated = await _service.UpdateCampRoleDefinitionAsync(
            roleDefinitionId: created.Id,
            name: "Renamed",
            description: "new desc",
            slotCount: 3,
            minimumRequired: 2,
            sortOrder: 50,
            isRequired: true,
            actorUserId: actorUserId);

        updated.Name.Should().Be("Renamed");
        updated.Description.Should().Be("new desc");
        updated.SlotCount.Should().Be(3);
        updated.MinimumRequired.Should().Be(2);
        updated.SortOrder.Should().Be(50);
        updated.IsRequired.Should().BeTrue();

        var row = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        row.Name.Should().Be("Renamed");
        row.Description.Should().Be("new desc");
        row.SlotCount.Should().Be(3);
        row.MinimumRequired.Should().Be(2);
        row.SortOrder.Should().Be(50);
        row.IsRequired.Should().BeTrue();
        row.CreatedAt.Should().Be(createdAt);
        row.UpdatedAt.Should().Be(afterAdvance);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // Deactivate / Reactivate
    // ==========================================================================

    [Fact]
    public async Task DeactivateCampRoleDefinitionAsync_SetsDeactivatedAt_AndReactivateClearsIt()
    {
        var actorUserId = Guid.NewGuid();
        var created = await _service.CreateCampRoleDefinitionAsync(
            "Toggle", null, 1, 0, 0, false, actorUserId);

        _clock.AdvanceMinutes(15);
        var deactivatedExpectedAt = _clock.GetCurrentInstant();

        await _service.DeactivateCampRoleDefinitionAsync(created.Id, actorUserId);

        var afterDeactivate = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        afterDeactivate.DeactivatedAt.Should().Be(deactivatedExpectedAt);
        afterDeactivate.UpdatedAt.Should().Be(deactivatedExpectedAt);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionDeactivated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());

        _clock.AdvanceMinutes(15);
        var reactivatedExpectedAt = _clock.GetCurrentInstant();

        await _service.ReactivateCampRoleDefinitionAsync(created.Id, actorUserId);

        var afterReactivate = await _dbContext.CampRoleDefinitions.FirstAsync(d => d.Id == created.Id);
        afterReactivate.DeactivatedAt.Should().BeNull();
        afterReactivate.UpdatedAt.Should().Be(reactivatedExpectedAt);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition),
            created.Id,
            Arg.Any<string>(),
            actorUserId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // GetCampRoleDefinitionsAsync
    // ==========================================================================

    [Fact]
    public async Task GetCampRoleDefinitionsAsync_RespectsIncludeDeactivatedFlag()
    {
        var actorUserId = Guid.NewGuid();

        // Create two locally-named roles so we can assert about them specifically
        // (the in-memory provider seeds the 6 default roles via HasData).
        var active = await _service.CreateCampRoleDefinitionAsync(
            "Active", null, 1, 0, 1000, false, actorUserId);
        var dead = await _service.CreateCampRoleDefinitionAsync(
            "Dead", null, 1, 0, 1001, false, actorUserId);

        await _service.DeactivateCampRoleDefinitionAsync(dead.Id, actorUserId);

        var withoutDeactivated = await _service.GetCampRoleDefinitionsAsync(includeDeactivated: false);
        var localWithout = withoutDeactivated.Where(d =>
            string.Equals(d.Name, "Active", StringComparison.Ordinal) ||
            string.Equals(d.Name, "Dead", StringComparison.Ordinal)).ToList();
        localWithout.Should().ContainSingle();
        localWithout[0].Name.Should().Be("Active");

        var withDeactivated = await _service.GetCampRoleDefinitionsAsync(includeDeactivated: true);
        var localWith = withDeactivated.Where(d =>
            string.Equals(d.Name, "Active", StringComparison.Ordinal) ||
            string.Equals(d.Name, "Dead", StringComparison.Ordinal)).ToList();
        localWith.Should().HaveCount(2);
        localWith.Select(d => d.Name).Should().Contain(["Active", "Dead"]);

        // Ordering: results sorted by SortOrder ascending across the full set
        withDeactivated.Select(d => d.SortOrder).Should().BeInAscendingOrder();
    }
}

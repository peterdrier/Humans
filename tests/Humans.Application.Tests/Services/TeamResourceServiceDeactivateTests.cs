using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Coverage for <see cref="ITeamResourceService.DeactivateResourcesForTeamAsync"/> — the
/// side of #494 that actually flips <c>IsActive</c> and writes audit entries. Uses
/// <see cref="StubTeamResourceService"/> so the test doesn't need real Google credentials;
/// the relevant method body (deactivation + audit) is logically identical to
/// <see cref="TeamResourceService"/>.
/// </summary>
public class TeamResourceServiceDeactivateTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLogService;
    private readonly StubTeamResourceService _service;

    public TeamResourceServiceDeactivateTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 15, 12, 0));
        _auditLogService = Substitute.For<IAuditLogService>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IAuditLogService)).Returns(_auditLogService);
        serviceProvider.GetService(typeof(ITeamService)).Returns(Substitute.For<ITeamService>());

        _service = new StubTeamResourceService(
            _dbContext,
            Options.Create(new TeamResourceManagementSettings()),
            serviceProvider,
            Substitute.For<IRoleAssignmentService>(),
            _clock,
            NullLogger<StubTeamResourceService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DeactivateResourcesForTeamAsync_FlipsIsActiveAndLogsAudit()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        SeedTeam(teamId, "Doomed");
        SeedTeam(otherTeamId, "Safe");

        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        SeedResource(otherTeamId, "Safe Drive", GoogleResourceType.DriveFolder);
        // Already-inactive row on target team should not generate a duplicate audit.
        SeedResource(teamId, "Already inactive", GoogleResourceType.DriveFolder, isActive: false);
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        var doomedRows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync();
        doomedRows.Should().OnlyContain(r => !r.IsActive);

        var safeRow = await _dbContext.GoogleResources
            .AsNoTracking()
            .SingleAsync(r => r.TeamId == otherTeamId);
        safeRow.IsActive.Should().BeTrue();

        // Exactly two audit entries (for the two previously-active doomed resources).
        await _auditLogService.Received(2).LogAsync(
            AuditAction.GoogleResourceDeactivated,
            nameof(GoogleResource),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task DeactivateResourcesForTeamAsync_WithResourceType_OnlyFlipsMatchingType()
    {
        // Guards the reconciliation-ordering bug: the nightly job runs DriveFolder then
        // Group, so when the Drive pass calls this, it must NOT touch the team's Group
        // row — otherwise the Group pass filters r.IsActive, skips it, and leaves
        // Group membership in place.
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Doomed");
        SeedResource(teamId, "Doomed Drive", GoogleResourceType.DriveFolder);
        SeedResource(teamId, "Doomed Group", GoogleResourceType.Group);
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId, GoogleResourceType.DriveFolder);

        var rows = await _dbContext.GoogleResources
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.ResourceType == GoogleResourceType.DriveFolder).IsActive.Should().BeFalse();
        rows.Single(r => r.ResourceType == GoogleResourceType.Group).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeactivateResourcesForTeamAsync_NoActiveResources_IsNoOp()
    {
        var teamId = Guid.NewGuid();
        SeedTeam(teamId, "Empty");
        await _dbContext.SaveChangesAsync();

        await _service.DeactivateResourcesForTeamAsync(teamId);

        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    private void SeedTeam(Guid id, string name)
    {
        _dbContext.Teams.Add(new Team
        {
            Id = id,
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
    }

    private void SeedResource(Guid teamId, string name, GoogleResourceType type, bool isActive = true)
    {
        _dbContext.GoogleResources.Add(new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            GoogleId = Guid.NewGuid().ToString(),
            Url = $"https://example.com/{name}",
            ResourceType = type,
            IsActive = isActive,
            ProvisionedAt = _clock.GetCurrentInstant()
        });
    }
}

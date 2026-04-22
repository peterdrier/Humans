using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Regression tests for <see cref="SystemTeamSyncJob.SyncBarrioLeadsMembershipForUserAsync"/>.
/// Covers #498: duplicate camp registration for a user who is already an active member of the
/// Barrio Leads system team must not violate IX_team_members_active_unique.
/// </summary>
public class SystemTeamSyncJobBarrioLeadsTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly SystemTeamSyncJob _job;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public SystemTeamSyncJobBarrioLeadsTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 15, 12, 0));

        var factory = new TestDbContextFactory(options);
        var campRepo = new CampRepository(factory);

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMembershipCalculator>());
        var provider = services.BuildServiceProvider();

        _job = new SystemTeamSyncJob(
            _dbContext,
            campRepo,
            provider,
            Substitute.For<IGoogleSyncService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            _cache,
            Substitute.For<IHumansMetrics>(),
            NullLogger<SystemTeamSyncJob>.Instance,
            _clock);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SyncBarrioLeadsMembershipForUserAsync_UserAlreadyActiveMember_IsNoOp()
    {
        // Arrange: user is lead of one camp and already has an active team_members row
        // for the Barrio Leads system team (as they would after registering a first camp).
        var userId = Guid.NewGuid();
        var team = await SeedBarrioLeadsTeamAsync();
        var camp1 = await SeedCampAsync("camp-one");
        var camp2 = await SeedCampAsync("camp-two");

        _dbContext.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp1.Id,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = _clock.GetCurrentInstant()
        });
        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        // Simulate a second camp registration: add another CampLead row, then call sync.
        _dbContext.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp2.Id,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        // Act + Assert: should NOT throw (previously failed with unique-index violation)
        // and there should still be exactly one active membership after sync.
        var act = async () => await _job.SyncBarrioLeadsMembershipForUserAsync(userId);
        await act.Should().NotThrowAsync();

        var activeRows = await _dbContext.TeamMembers
            .Where(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt == null)
            .CountAsync();
        activeRows.Should().Be(1);
    }

    [Fact]
    public async Task SyncBarrioLeadsMembershipForUserAsync_UserBecomesLead_AddsMembership()
    {
        // Arrange: team exists, user has a new CampLead but no TeamMember row yet.
        var userId = Guid.NewGuid();
        var team = await SeedBarrioLeadsTeamAsync();
        var camp = await SeedCampAsync("new-camp");

        _dbContext.CampLeads.Add(new CampLead
        {
            Id = Guid.NewGuid(),
            CampId = camp.Id,
            UserId = userId,
            Role = CampLeadRole.CoLead,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        // Act
        await _job.SyncBarrioLeadsMembershipForUserAsync(userId);

        // Assert: single active membership created.
        var activeRows = await _dbContext.TeamMembers
            .Where(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt == null)
            .CountAsync();
        activeRows.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Seed helpers
    // ------------------------------------------------------------------

    private async Task<Team> SeedBarrioLeadsTeamAsync()
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Barrio Leads",
            Slug = "barrio-leads",
            Description = "System team for barrio leads",
            SystemTeamType = SystemTeamType.BarrioLeads,
            IsActive = true,
            IsHidden = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();
        return team;
    }

    private async Task<Camp> SeedCampAsync(string slug)
    {
        var camp = new Camp
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            ContactEmail = $"{slug}@example.com",
            ContactPhone = "+34600000000",
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Camps.Add(camp);
        await _dbContext.SaveChangesAsync();
        return camp;
    }
}

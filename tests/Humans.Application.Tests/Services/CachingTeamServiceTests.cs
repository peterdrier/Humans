using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Teams;
using Humans.Infrastructure.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public sealed class CachingTeamServiceTests : IDisposable
{
    private readonly DbContextOptions<HumansDbContext> _options;
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly ServiceProvider _serviceProvider;
    private readonly CachingTeamService _service;

    public CachingTeamServiceTests()
    {
        _options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(_options);

        var userService = Substitute.For<IUserService>();
        userService
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult(LoadUsers(ids, includeEmails: false));
            });
        userService
            .GetByIdsWithEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult(LoadUsers(ids, includeEmails: true));
            });
        userService.StubGetUserInfosFromDb(_options);

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddKeyedScoped<ITeamService>(
            CachingTeamService.InnerServiceKey,
            (_, _) => Substitute.For<ITeamService>());
        _serviceProvider = services.BuildServiceProvider();

        ITeamRepository teamRepository = new TeamRepository(new TestDbContextFactory(_options));
        _service = new CachingTeamService(
            teamRepository,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingTeamService>.Instance);
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveDirectCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var inactiveTeam = SeedTeam("Inactive", isActive: false);
        SeedTeamMember(inactiveTeam.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(inactiveTeam.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_InactiveParentCoordinator_DoesNotGrantChildAccess()
    {
        var user = SeedUser();
        var inactiveParent = SeedTeam("Inactive Parent", isActive: false);
        var child = SeedTeam("Child");
        child.ParentTeamId = inactiveParent.Id;
        SeedTeamMember(inactiveParent.Id, user.Id, TeamMemberRole.Coordinator);
        await _dbContext.SaveChangesAsync();

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesCanonicalUserEmail()
    {
        var user = SeedUser("Alice");
        user.Email = null;
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "alice@example.test",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Member);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetTeamAsync(team.Id);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.Email.Should().Be("alice@example.test");
    }

    private IReadOnlyDictionary<Guid, User> LoadUsers(IReadOnlyCollection<Guid> ids, bool includeEmails)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, User>();

        using var db = new HumansDbContext(_options);
        var users = db.Users.AsNoTracking();
        if (includeEmails)
            users = users.Include(u => u.UserEmails);

        return users
            .Where(u => ids.Contains(u.Id))
            .ToDictionary(u => u.Id);
    }

    private User SeedUser(string displayName = "Test User")
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private Team SeedTeam(string name, bool isActive = true)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            IsActive = isActive,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedTeamMember(Guid teamId, Guid userId, TeamMemberRole role)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamMembers.Add(member);
        return member;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }
}

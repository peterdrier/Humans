using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideRoleResolverTests : IDisposable
{
    private readonly HumansDbContext _db;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 21, 12, 0));

    public GuideRoleResolverTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new HumansDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private ClaimsPrincipal PrincipalWithRoles(Guid? userId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: userId is null ? null : "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task Resolve_Anonymous_ReturnsAnonymousContext()
    {
        var resolver = new GuideRoleResolver(_db);

        var result = await resolver.ResolveAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        result.IsAuthenticated.Should().BeFalse();
        result.IsTeamCoordinator.Should().BeFalse();
        result.SystemRoles.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_AuthWithAdminRole_ReportsSystemRoles()
    {
        var resolver = new GuideRoleResolver(_db);
        var user = PrincipalWithRoles(Guid.NewGuid(), RoleNames.Admin, RoleNames.Board);

        var result = await resolver.ResolveAsync(user);

        result.IsAuthenticated.Should().BeTrue();
        result.SystemRoles.Should().Contain([RoleNames.Admin, RoleNames.Board]);
    }

    [Fact]
    public async Task Resolve_ActiveTeamCoordinator_IsTeamCoordinatorTrue()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _db.Teams.Add(new Team
        {
            Id = teamId,
            Name = "T",
            Slug = "t",
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _db.SaveChangesAsync(CancellationToken.None);

        var resolver = new GuideRoleResolver(_db);
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_FormerTeamCoordinator_IsTeamCoordinatorFalse()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _db.Teams.Add(new Team
        {
            Id = teamId,
            Name = "T",
            Slug = "t",
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = _clock.GetCurrentInstant(),
            LeftAt = _clock.GetCurrentInstant()
        });
        await _db.SaveChangesAsync(CancellationToken.None);

        var resolver = new GuideRoleResolver(_db);
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeFalse();
    }

    [Fact]
    public async Task Resolve_MemberButNotCoordinator_IsTeamCoordinatorFalse()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _db.Teams.Add(new Team
        {
            Id = teamId,
            Name = "T",
            Slug = "t",
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _db.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        });
        await _db.SaveChangesAsync(CancellationToken.None);

        var resolver = new GuideRoleResolver(_db);
        var user = PrincipalWithRoles(userId);

        var result = await resolver.ResolveAsync(user, CancellationToken.None);

        result.IsTeamCoordinator.Should().BeFalse();
    }
}

using Humans.Auth.Domain;
using Humans.Auth.Data;
using Humans.Auth.Contracts;
using Humans.Audit.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Google.Data;
using Humans.Users.Contracts;
using Humans.Users.Data;

using Humans.Teams.Contracts;
namespace Humans.Teams.Tests.Infrastructure;

public abstract class TeamsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private readonly SectionTestDbs _dbs = new();

    private readonly Lazy<SectionTestDb<AuthDbContext>> _authDb;
    private protected AuthDbContext AuthDb => _authDb.Value.Context;
    private protected TestDbContextFactory<AuthDbContext> AuthDbFactory => _authDb.Value.Factory;

    private readonly Lazy<SectionTestDb<GoogleIntegrationDbContext>> _googleIntegrationDb;
    private protected GoogleIntegrationDbContext GoogleDb => _googleIntegrationDb.Value.Context;
    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleDbFactory => _googleIntegrationDb.Value.Factory;

    private readonly Lazy<SectionTestDb<TeamsDbContext>> _teamsDb;
    private protected TeamsDbContext TeamsDb => _teamsDb.Value.Context;
    private protected TestDbContextFactory<TeamsDbContext> TeamsDbFactory => _teamsDb.Value.Factory;

    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    // xUnit builds a fresh class instance per test, so these substitutes never leak state.

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();

    protected TeamsTestHarness(Instant? now = null)
    {
        DbOptions = TestDbOptions.InMemory<UsersDbContext>();
        Db = new UsersDbContext(DbOptions);
        _dbs.Track(Db);

        _authDb = _dbs.Add<AuthDbContext>(o => new(o));
        _googleIntegrationDb = _dbs.Add<GoogleIntegrationDbContext>(o => new(o));
        _teamsDb = _dbs.Add<TeamsDbContext>(o => new(o));

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>Flushes <see cref="Db"/> and every section context, so a test need not track where each seed landed.</summary>
    private protected Task SaveAllAsync(CancellationToken ct = default) => _dbs.SaveAllAsync(ct);

    /// <summary>
    /// Clears the tracker on <see cref="Db"/> and every section context: clearing only
    /// <see cref="Db"/> silently returns stale state for entities another context owns.
    /// </summary>
    private protected void ClearAllTrackers() => _dbs.ClearTrackers();

    public virtual void Dispose()
    {
        Cache.Dispose();
        _dbs.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>An <see cref="IUserService"/> whose user-info reads come from this harness's in-memory DB.</summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.StubGetUserInfoFromContext(Db);
        svc.StubGetUserInfosFromDb(DbOptions);
        svc.StubAllUserInfosFromDb(DbOptions);

        return svc;
    }

    // Seeders add to Db without saving: callers stage several, then save once.

    protected User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en",
            // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
            BurnerName = displayName,
        };
        LegacyDisplayNameProperty.SetValue(user, displayName);
        Db.Users.Add(user);
        return user;
    }

    protected User SeedUser(string displayName) => SeedUser(null, displayName);

    private protected Team SeedTeam(Guid teamId, string name) =>
        SeedTeam(name, SystemTeamType.None, teamId);

    private protected Team SeedTeam(
        string name,
        SystemTeamType type = SystemTeamType.None,
        Guid? id = null,
        bool isActive = true,
        bool requiresApproval = false)
    {
        var team = new Team
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SystemTeamType = type,
            IsActive = isActive,
            RequiresApproval = requiresApproval,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        TeamsDb.Teams.Add(team);
        return team;
    }

    private protected TeamMember SeedTeamMember(
        Guid teamId,
        Guid userId,
        TeamMemberRole role = TeamMemberRole.Member,
        Instant? leftAt = null)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Team = TeamsDb.Teams.Local.Single(t => t.Id == teamId),
            UserId = userId,
            Role = role,
            JoinedAt = Clock.GetCurrentInstant(),
            LeftAt = leftAt
        };
        TeamsDb.TeamMembers.Add(member);
        return member;
    }

    private protected SystemRole SeedSystemRole(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null)
    {
        var ra = new SystemRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        };
        // Unlike the Db seeders above, this one saves: auth_role_assignments sits in its own
        // context, so a caller's Db.SaveChangesAsync() would never reach it.
        AuthDb.SystemRoles.Add(ra);
        AuthDb.SaveChanges();
        return ra;
    }

    private protected TeamJoinRequest SeedJoinRequest(
        Guid teamId,
        Guid userId,
        TeamJoinRequestStatus status = TeamJoinRequestStatus.Pending)
    {
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Status = status,
            RequestedAt = Clock.GetCurrentInstant()
        };
        TeamsDb.TeamJoinRequests.Add(request);
        return request;
    }
}

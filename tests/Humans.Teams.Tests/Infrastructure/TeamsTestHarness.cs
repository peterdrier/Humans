using Humans.Auth.Domain;
using Humans.Auth.Data;
using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.GoogleIntegration.Data;
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
    private protected TestDbContextFactory DbFactory { get; }

    // Section contexts are separate in-memory stores: a test seeding across two saves each.
    // Every pair is built on first touch: eager construction gave each test its own EF
    // service provider, which pushed a 5s-budget test past its CI timeout.

    private readonly List<Func<DbContext?>> _sectionContextProbes = [];

    private readonly Lazy<SectionDb<AuthDbContext>> _authDb;
    private protected AuthDbContext AuthDb => _authDb.Value.Context;
    private protected TestDbContextFactory<AuthDbContext> AuthDbFactory => _authDb.Value.Factory;

    private readonly Lazy<SectionDb<GoogleIntegrationDbContext>> _googleIntegrationDb;
    private protected GoogleIntegrationDbContext GoogleIntegrationDb => _googleIntegrationDb.Value.Context;
    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleIntegrationDbFactory => _googleIntegrationDb.Value.Factory;

    private readonly Lazy<SectionDb<ShiftsDbContext>> _shiftsDb;
    private protected ShiftsDbContext ShiftsDb => _shiftsDb.Value.Context;
    private protected TestDbContextFactory<ShiftsDbContext> ShiftsDbFactory => _shiftsDb.Value.Factory;

    private readonly Lazy<SectionDb<TeamsDbContext>> _teamsDb;
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
        DbOptions = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new UsersDbContext(DbOptions);
        DbFactory = new TestDbContextFactory(DbOptions);

        _authDb = RegisterSection<AuthDbContext>(o => new(o));
        _googleIntegrationDb = RegisterSection<GoogleIntegrationDbContext>(o => new(o));
        _shiftsDb = RegisterSection<ShiftsDbContext>(o => new(o));
        _teamsDb = RegisterSection<TeamsDbContext>(o => new(o));

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>A peeled section's in-memory context and the factory over the same store.</summary>
    private sealed record SectionDb<TContext>(TContext Context, TestDbContextFactory<TContext> Factory)
        where TContext : DbContext;

    private Lazy<SectionDb<TContext>> RegisterSection<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        var lazy = new Lazy<SectionDb<TContext>>(() =>
        {
            var options = NewSectionDbOptions<TContext>();
            return new SectionDb<TContext>(create(options), new TestDbContextFactory<TContext>(options));
        });

        _sectionContextProbes.Add(() => lazy.IsValueCreated ? lazy.Value.Context : null);
        return lazy;
    }

    private IEnumerable<DbContext> CreatedSectionContexts() =>
        _sectionContextProbes.Select(probe => probe()).OfType<DbContext>();

    /// <summary>
    /// Pair with <c>new TestDbContextFactory&lt;TContext&gt;(options)</c> for the repository under
    /// test, and construct a context directly from the options for seeding.
    /// </summary>
    private protected static DbContextOptions<TContext> NewSectionDbOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>Flushes <see cref="Db"/> and every section context, so a test need not track where each seed landed.</summary>
    private protected async Task SaveAllAsync(CancellationToken ct = default)
    {
        await Db.SaveChangesAsync(ct);
        foreach (var sectionDb in CreatedSectionContexts())
        {
            await sectionDb.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Clears the tracker on <see cref="Db"/> and every section context: clearing only
    /// <see cref="Db"/> silently returns stale state for entities another context owns.
    /// </summary>
    private protected void ClearAllTrackers()
    {
        Db.ChangeTracker.Clear();
        foreach (var sectionDb in CreatedSectionContexts())
        {
            sectionDb.ChangeTracker.Clear();
        }
    }

    public virtual void Dispose()
    {
        Cache.Dispose();
        Db.Dispose();
        foreach (var sectionDb in CreatedSectionContexts())
        {
            sectionDb.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>An <see cref="IUserService"/> whose user-info reads come from this harness's in-memory DB.</summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.StubGetUserInfoFromContext(Db);
        svc.StubGetUserInfosFromDb(DbOptions);
        svc.StubGetAllUserInfosFromDb(DbOptions);

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

    private protected RoleAssignment SeedRoleAssignment(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null)
    {
        var ra = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = Guid.NewGuid()
        };
        // Unlike the Db seeders above, this one saves: role_assignments sits in its own
        // context, so a caller's Db.SaveChangesAsync() would never reach it.
        AuthDb.RoleAssignments.Add(ra);
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

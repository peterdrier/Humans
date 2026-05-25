using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Infrastructure;

/// <summary>
/// Base class for service tests. Owns the per-test in-memory <see cref="HumansDbContext"/>,
/// an <see cref="IDbContextFactory{TContext}"/>, a deterministic <see cref="FakeClock"/>,
/// and an <see cref="IMemoryCache"/>, plus the most common entity seeders. Tests construct
/// their service-under-test in their own ctor using these resources; the harness does not
/// pre-build the service.
/// </summary>
public abstract class ServiceTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<HumansDbContext> DbOptions { get; }
    private protected HumansDbContext Db { get; }
    private protected TestDbContextFactory DbFactory { get; }
    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    // ----- Shared NSubstitute stubs -----------------------------------------
    // Bare substitutes for the four interfaces that ~30 service tests stub
    // identically. xUnit creates a fresh test class instance per test, so
    // these are per-test-fresh — no state leak across tests. Override behavior
    // in a derived ctor via `.When(...).Do(...)` or `.Returns(...)` if needed
    // (e.g., TeamServiceTests redirects ShiftAuthInvalidator to Cache).

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IShiftAuthorizationInvalidator ShiftAuthInvalidator { get; } = Substitute.For<IShiftAuthorizationInvalidator>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();

    protected ServiceTestHarness(Instant? now = null)
    {
        DbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new HumansDbContext(DbOptions);
        DbFactory = new TestDbContextFactory(DbOptions);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    public virtual void Dispose()
    {
        Cache.Dispose();
        Db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates an NSubstitute <see cref="IUserService"/> whose reader methods
    /// (<c>GetByIdsAsync</c>, <c>GetUserInfoAsync</c>, <c>GetUserInfosAsync</c>)
    /// are wired to read from this harness's in-memory DB.
    /// Mirrors the production behavior of the User stitcher without requiring the real
    /// caching/repository stack. Use for services that depend on <see cref="IUserService"/>
    /// for cross-domain user lookups.
    /// </summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                if (ids.Count == 0)
                    return Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                        new Dictionary<Guid, User>());
                using var db = new HumansDbContext(DbOptions);
                var users = db.Users.AsNoTracking()
                    .Include(u => u.UserEmails)
                    .Where(u => ids.Contains(u.Id))
                    .ToList();
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                    users.ToDictionary(u => u.Id));
            });

        svc.StubGetUserInfoFromContext(Db);
        svc.StubGetUserInfosFromDb(DbOptions);

        return svc;
    }

    // ----- Common entity seeders ------------------------------------------------
    // Add to Db but do not SaveChanges — callers stage multiple seeds, then await
    // Db.SaveChangesAsync() once. Matches the existing per-file pattern.

    protected User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var userId = id ?? Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        LegacyDisplayNameProperty.SetValue(user, displayName);
        Db.Users.Add(user);
        return user;
    }

    /// <summary>
    /// Positional-displayName overload — absorbs <c>SeedUser("Alice")</c> call sites
    /// without forcing migration to named args.
    /// </summary>
    protected User SeedUser(string displayName) => SeedUser(null, displayName);

    /// <summary>
    /// Id-first overload — absorbs <c>SeedTeam(teamId, "name")</c> call sites that
    /// pre-existing local helpers used.
    /// </summary>
    protected Team SeedTeam(Guid teamId, string name) =>
        SeedTeam(name, SystemTeamType.None, teamId);

    protected Team SeedTeam(
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
        Db.Teams.Add(team);
        return team;
    }

    protected TeamMember SeedTeamMember(
        Guid teamId,
        Guid userId,
        TeamMemberRole role = TeamMemberRole.Member,
        Instant? leftAt = null)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Team = Db.Teams.Local.Single(t => t.Id == teamId),
#pragma warning disable CS0618 // TeamMember.User is Obsolete; tests seed nav for DB-roundtrip verification.
            UserId = userId,
            User = Db.Users.Local.Single(u => u.Id == userId),
#pragma warning restore CS0618
            Role = role,
            JoinedAt = Clock.GetCurrentInstant(),
            LeftAt = leftAt
        };
        Db.TeamMembers.Add(member);
        return member;
    }

    protected RoleAssignment SeedRoleAssignment(
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
        Db.RoleAssignments.Add(ra);
        return ra;
    }

    protected TeamJoinRequest SeedJoinRequest(
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
        Db.TeamJoinRequests.Add(request);
        return request;
    }
}

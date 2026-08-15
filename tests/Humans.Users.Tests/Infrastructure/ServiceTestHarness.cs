using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// Base class for service tests. Owns the per-test in-memory <see cref="UsersDbContext"/>,
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

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private protected TestDbContextFactory DbFactory { get; }

    // ----- Peeled-section contexts (nobodies-collective/Humans#858) ----------
    // These sections' tables are no longer in Db's model, so seeding them and
    // wiring their repositories both go through the section context. Each pair
    // is an independent in-memory store; a test that seeds across two saves on
    // each. Add a pair here as each further section peels.
    //
    // Every pair is built on FIRST TOUCH, not in the constructor: this class is
    // the base for ~4000 tests and almost none of them touch any given section.
    // Building all of them eagerly gives each test six extra sets of
    // DbContextOptions — each with its own EF internal service provider — and
    // that overhead alone pushed a 5s-budget test over its timeout on CI.
    // SaveAllAsync/ClearAllTrackers/Dispose therefore visit only the pairs a
    // test actually created.

    private readonly List<Func<DbContext?>> _sectionContextProbes = [];




    /// <summary>Teams: <c>teams</c>, <c>team_members</c>, <c>team_join_requests</c>,
    /// <c>team_join_request_state_history</c>, <c>team_role_definitions</c>,
    /// <c>team_role_assignments</c>, <c>team_early_entry_grants</c>
    /// (see <see cref="SeedTeam(string, SystemTeamType, Guid?, bool, bool)"/>).</summary>
    private readonly Lazy<SectionDb<TeamsDbContext>> _teamsDb;
    private protected TeamsDbContext TeamsDb => _teamsDb.Value.Context;
    private protected TestDbContextFactory<TeamsDbContext> TeamsDbFactory => _teamsDb.Value.Factory;

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
    private protected IEarlyEntryInvalidator EarlyEntryInvalidator { get; } = Substitute.For<IEarlyEntryInvalidator>();

    protected ServiceTestHarness(Instant? now = null)
    {
        DbOptions = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new UsersDbContext(DbOptions);
        DbFactory = new TestDbContextFactory(DbOptions);

        _teamsDb = RegisterSection<TeamsDbContext>(o => new(o));

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>A peeled section's in-memory context and the factory over the same store.</summary>
    private sealed record SectionDb<TContext>(TContext Context, TestDbContextFactory<TContext> Factory)
        where TContext : DbContext;

    /// <summary>
    /// Declares a peeled-section context without building it. The options, the
    /// context and the factory are all created the first time a test reads the
    /// corresponding property; until then the section costs nothing.
    /// </summary>
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

    /// <summary>The section contexts this test actually touched, in declaration order.</summary>
    private IEnumerable<DbContext> CreatedSectionContexts() =>
        _sectionContextProbes.Select(probe => probe()).OfType<DbContext>();

    /// <summary>
    /// In-memory options for a per-section DbContext (nobodies-collective/Humans#858),
    /// e.g. <c>ContainersDbContext</c>. Pair with
    /// <c>new TestDbContextFactory&lt;TContext&gt;(options)</c> for the repository under
    /// test and construct a context directly from the options for seeding.
    /// </summary>
    private protected static DbContextOptions<TContext> NewSectionDbOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>
    /// Flushes <see cref="Db"/> and every peeled-section context. A test that
    /// stages rows across the main pile and one or more section contexts calls
    /// this instead of <c>Db.SaveChangesAsync</c>, so it does not have to track
    /// which context each seed landed in. <c>SaveChanges</c> on a context with
    /// no pending changes is a no-op, so this is safe everywhere.
    /// </summary>
    private protected async Task SaveAllAsync(CancellationToken ct = default)
    {
        await Db.SaveChangesAsync(ct);
        foreach (var sectionDb in CreatedSectionContexts())
        {
            await sectionDb.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Clears the change tracker on <see cref="Db"/> and every peeled-section
    /// context. Tests do this after exercising a service so the read-back sees
    /// what the repository actually wrote rather than the instance the seed
    /// left tracked — and the entity now lives in whichever context owns its
    /// section, so clearing only <see cref="Db"/> silently returns stale state.
    /// </summary>
    private protected void ClearAllTrackers()
    {
        Db.ChangeTracker.Clear();
        foreach (var sectionDb in CreatedSectionContexts())
        {
            sectionDb.ChangeTracker.Clear();
        }
    }

    /// <summary>Synchronous <see cref="SaveAllAsync"/>.</summary>
    private protected void SaveAll()
    {
        Db.SaveChanges();
        foreach (var sectionDb in CreatedSectionContexts())
        {
            sectionDb.SaveChanges();
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

    /// <summary>
    /// Creates an NSubstitute <see cref="IUserService"/> whose reader methods
    /// (<c>GetUserInfoAsync</c>, <c>GetUserInfosAsync</c>)
    /// are wired to read from this harness's in-memory DB.
    /// Mirrors the production behavior of the User stitcher without requiring the real
    /// caching/repository stack. Use for services that depend on <see cref="IUserService"/>
    /// for cross-domain user lookups.
    /// </summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.StubGetUserInfoFromContext(Db);
        svc.StubGetUserInfosFromDb(DbOptions);
        svc.StubGetAllUserInfosFromDb(DbOptions);

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

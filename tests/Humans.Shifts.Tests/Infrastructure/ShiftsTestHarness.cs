using Humans.Shifts.Tests.Infrastructure;
using Humans.Application;
using Humans.EarlyEntry.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
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

using Humans.Teams.Contracts;
namespace Humans.Shifts.Tests.Infrastructure;

/// <summary>
/// Base class for the section's service and repository tests: the per-test in-memory
/// <see cref="UsersDbContext"/>, <see cref="ShiftsDbContext"/> and
/// <see cref="TeamsDbContext"/>, a deterministic <see cref="FakeClock"/>, an
/// <see cref="IMemoryCache"/>, and the seeders and shared substitutes the moved tests use.
/// </summary>
/// <remarks>
/// A trimmed <em>copy</em> of <c>Humans.Application.Tests</c>' <c>ServiceTestHarness</c>,
/// not a share: the moved tests make 169 <c>SaveAllAsync</c>, 56 <c>SeedUser</c> and 55
/// <c>SeedTeam</c> calls, which is Teams' "copy the harness" rather than Budget's "own
/// three members" (design §15 step 8). The Google-integration and Camps section pairs are
/// dropped — no Shifts test touches them.
///
/// <para>
/// The two <c>InternalsVisibleTo</c> entries this needs —
/// <c>Humans.Infrastructure</c> → <c>Humans.Shifts.Tests</c> for
/// <see cref="UsersDbContext"/>, and <c>Humans.Teams</c> → <c>Humans.Shifts.Tests</c> for
/// <see cref="TeamsDbContext"/> and <see cref="Team"/> — are Teams finding 36's shape and
/// come out when these fixtures stop seeding other sections' tables directly.
/// </para>
/// </remarks>
public abstract class ShiftsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private protected TestDbContextFactory<UsersDbContext> DbFactory { get; }

    // Every pair is built on FIRST TOUCH, not in the constructor — see ServiceTestHarness'
    // note: eager construction gives each test extra DbContextOptions, each with its own
    // EF internal service provider, and that overhead alone pushed a 5s-budget test over
    // its timeout on CI.
    private readonly List<Func<DbContext?>> _sectionContextProbes = [];

    /// <summary>Shifts: <c>event_settings</c>, <c>rotas</c>, <c>shifts</c>, <c>shift_signups</c>,
    /// <c>shift_tags</c>, <c>rota_shift_tags</c>, <c>volunteer_event_profiles</c>,
    /// <c>general_availability</c>, <c>volunteer_build_statuses</c>, <c>volunteer_tag_preferences</c>.</summary>
    private readonly Lazy<SectionDb<ShiftsDbContext>> _shiftsDb;
    private protected ShiftsDbContext ShiftsDb => _shiftsDb.Value.Context;
    private protected TestDbContextFactory<ShiftsDbContext> ShiftsDbFactory => _shiftsDb.Value.Factory;

    /// <summary>Teams: <c>teams</c>, <c>team_members</c>, <c>team_join_requests</c> and friends.</summary>
    private readonly Lazy<SectionDb<TeamsDbContext>> _teamsDb;
    private protected TeamsDbContext TeamsDb => _teamsDb.Value.Context;
    private protected TestDbContextFactory<TeamsDbContext> TeamsDbFactory => _teamsDb.Value.Factory;

    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IShiftAuthorizationInvalidator ShiftAuthInvalidator { get; } = Substitute.For<IShiftAuthorizationInvalidator>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();
    private protected IEarlyEntryInvalidator EarlyEntryInvalidator { get; } = Substitute.For<IEarlyEntryInvalidator>();

    protected ShiftsTestHarness(Instant? now = null)
    {
        DbOptions = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new UsersDbContext(DbOptions);
        DbFactory = new TestDbContextFactory<UsersDbContext>(DbOptions);

        _shiftsDb = RegisterSection<ShiftsDbContext>(o => new(o));
        _teamsDb = RegisterSection<TeamsDbContext>(o => new(o));

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

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

    private protected static DbContextOptions<TContext> NewSectionDbOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    /// <summary>Flushes <see cref="Db"/> and every section context the test touched.</summary>
    private protected async Task SaveAllAsync(CancellationToken ct = default)
    {
        await Db.SaveChangesAsync(ct);
        foreach (var sectionDb in CreatedSectionContexts())
        {
            await sectionDb.SaveChangesAsync(ct);
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

    /// <summary>
    /// Clears the change tracker on <see cref="Db"/> and every touched section context, so
    /// a read-back sees what the repository wrote rather than the tracked seed instance.
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

    /// <summary>
    /// An <see cref="IUserServiceRead"/> substitute whose batch read projects this
    /// harness's in-memory users. The section's only consumer is
    /// <c>WorkloadService</c>, which calls <c>GetUserInfosAsync</c> and reads display
    /// names off the result — Governance's "copy the projection" rather than sharing
    /// <c>UserInfoStubHelpers</c>, whose other half is built around a DbContext this
    /// project cannot see.
    /// </summary>
    private protected IUserServiceRead NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserServiceRead>();

        svc.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = Db.Users
                    .Where(u => ids.Contains(u.Id))
                    .ToList()
                    .ToDictionary(u => u.Id, ToUserInfo);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        svc.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var id = callInfo.Arg<Guid>();
                var user = Db.Users.FirstOrDefault(u => u.Id == id);
                return new ValueTask<UserInfo?>(user is null ? null : ToUserInfo(user));
            });

        return svc;
    }

    private static UserInfo ToUserInfo(User user) =>
        UserInfo.Create(user, [], [], [], null, [], [], [], []);

    // ----- Common entity seeders ------------------------------------------------
    // Add to the context but do not SaveChanges — callers stage multiple seeds, then
    // await SaveAllAsync() once.

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

    /// <summary>Positional-displayName overload — absorbs <c>SeedUser("Alice")</c> call sites.</summary>
    protected User SeedUser(string displayName) => SeedUser(null, displayName);

    /// <summary>Id-first overload — absorbs <c>SeedTeam(teamId, "name")</c> call sites.</summary>
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
}

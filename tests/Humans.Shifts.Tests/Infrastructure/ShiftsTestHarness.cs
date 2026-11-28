using Humans.Audit.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;
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
/// Seeding the Users and Teams tables directly needs their <c>InternalsVisibleTo</c> entries;
/// both go when these fixtures stop seeding other sections' tables.
/// </summary>
public abstract class ShiftsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private readonly SectionTestDbs _dbs = new();

    private readonly Lazy<SectionTestDb<ShiftsDbContext>> _shiftsDb;
    private protected ShiftsDbContext ShiftsDb => _shiftsDb.Value.Context;
    private protected TestDbContextFactory<ShiftsDbContext> ShiftsDbFactory => _shiftsDb.Value.Factory;

    private readonly Lazy<SectionTestDb<TeamsDbContext>> _teamsDb;
    private protected TeamsDbContext TeamsDb => _teamsDb.Value.Context;
    private protected TestDbContextFactory<TeamsDbContext> TeamsDbFactory => _teamsDb.Value.Factory;

    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();

    protected ShiftsTestHarness(Instant? now = null)
    {
        DbOptions = TestDbOptions.InMemory<UsersDbContext>();
        Db = new UsersDbContext(DbOptions);
        _dbs.Track(Db);

        _shiftsDb = _dbs.Add<ShiftsDbContext>(o => new(o));
        _teamsDb = _dbs.Add<TeamsDbContext>(o => new(o));

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>Flushes <see cref="Db"/> and every section context the test touched.</summary>
    private protected Task SaveAllAsync(CancellationToken ct = default) => _dbs.SaveAllAsync(ct);

    private protected void SaveAll() => _dbs.SaveAll();

    /// <summary>
    /// Clears the change tracker on <see cref="Db"/> and every touched section context, so
    /// a read-back sees what the repository wrote rather than the tracked seed instance.
    /// </summary>
    private protected void ClearAllTrackers() => _dbs.ClearTrackers();

    public virtual void Dispose()
    {
        Cache.Dispose();
        _dbs.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Settings' calendars, by event id, that <see cref="NewCalendarResolver"/> serves.</summary>
    private protected Dictionary<Guid, TestEventCalendar> Calendars { get; } = [];

    /// <summary>
    /// A resolver whose fake <see cref="ISettingsService"/> serves <see cref="Calendars"/> at call time, and whose
    /// Shifts overlay reads <see cref="ShiftsDb"/>.
    /// </summary>
    private protected EventCalendarResolver NewCalendarResolver()
    {
        var settingsService = Substitute.For<ISettingsService>();

        settingsService.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(
                Calendars.TryGetValue(ci.Arg<Guid>(), out var cal) ? cal.ToInfo() : null));

        settingsService.GetActiveEventSettings()
            .Returns(_ => Calendars.Values.FirstOrDefault(c => c.IsActive)?.ToInfo());

        return new EventCalendarResolver(settingsService, new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock));
    }

    /// <summary>An <see cref="IUserService"/> whose batch read projects this harness's in-memory users.</summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.GetUserInfosAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IEnumerable<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = Db.Users
                    .Where(u => ids.Contains(u.Id))
                    .ToList()
                    .ToDictionary(u => u.Id, u => UserInfoFixtures.FromUser(u));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        svc.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async ValueTask<UserInfo?> (callInfo) =>
            {
                var id = callInfo.Arg<Guid>();
                var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id, callInfo.Arg<CancellationToken>());
                return user is null ? null : UserInfoFixtures.FromUser(user);
            });

        return svc;
    }

    // Seeders add without saving: callers stage several, then await SaveAllAsync() once.

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
}

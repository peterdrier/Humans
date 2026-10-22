using Humans.EarlyEntry.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
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
    private protected TestDbContextFactory<UsersDbContext> DbFactory { get; }

    // Every pair is built on first touch: eager construction gives each test its own EF
    // internal service provider, which alone pushed a 5s-budget test past its CI timeout.
    private readonly List<Func<DbContext?>> _sectionContextProbes = [];

    private readonly Lazy<SectionDb<ShiftsDbContext>> _shiftsDb;
    private protected ShiftsDbContext ShiftsDb => _shiftsDb.Value.Context;
    private protected TestDbContextFactory<ShiftsDbContext> ShiftsDbFactory => _shiftsDb.Value.Factory;

    private readonly Lazy<SectionDb<TeamsDbContext>> _teamsDb;
    private protected TeamsDbContext TeamsDb => _teamsDb.Value.Context;
    private protected TestDbContextFactory<TeamsDbContext> TeamsDbFactory => _teamsDb.Value.Factory;

    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();

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
    /// A resolver whose fake <see cref="ISettingsService"/> serves the <see cref="EventSettings"/>
    /// rows seeded in <see cref="ShiftsDb"/> at call time.
    /// </summary>
    private protected EventCalendarResolver NewCalendarResolver()
    {
        var settingsService = Substitute.For<ISettingsService>();

        settingsService.GetEventSettingsByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var id = ci.Arg<Guid>();
                var local = ShiftsDb.EventSettings.Local.FirstOrDefault(e => e.Id == id)
                    ?? await ShiftsDb.EventSettings.FirstOrDefaultAsync(e => e.Id == id, ci.Arg<CancellationToken>());
                return local is null ? null : ToEventSettingsInfo(local);
            });

        settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var local = ShiftsDb.EventSettings.Local.FirstOrDefault(e => e.IsActive)
                    ?? await ShiftsDb.EventSettings.FirstOrDefaultAsync(e => e.IsActive, ci.Arg<CancellationToken>());
                return local is null ? null : ToEventSettingsInfo(local);
            });

        return new EventCalendarResolver(settingsService);
    }

    private static EventSettingsInfo ToEventSettingsInfo(EventSettings src) => new(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: src.TimeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
        FirstCrewStartOffset: src.FirstCrewStartOffset,
        SetupWeekStartOffset: src.SetupWeekStartOffset,
        PreEventWeekStartOffset: src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: src.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose);

    /// <summary>An <see cref="IUserService"/> whose batch read projects this harness's in-memory users.</summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();

        svc.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = callInfo.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = Db.Users
                    .Where(u => ids.Contains(u.Id))
                    .ToList()
                    .ToDictionary(u => u.Id, u => u.ToUserInfo());
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        svc.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async ValueTask<UserInfo?> (callInfo) =>
            {
                var id = callInfo.Arg<Guid>();
                var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id, callInfo.Arg<CancellationToken>());
                return user?.ToUserInfo();
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

using Humans.Audit.Contracts;
using Humans.Auth.Contracts;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Camps.Tests.Infrastructure;

/// <summary>
/// Base class for service tests. It does not pre-build the service under test: each test class
/// constructs its own from these resources.
/// </summary>
public abstract class CampsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private readonly SectionTestDbs _dbs = new();

    private readonly Lazy<SectionTestDb<CampsDbContext>> _campsDb;
    private protected CampsDbContext CampsDb => _campsDb.Value.Context;
    private protected TestDbContextFactory<CampsDbContext> CampsDbFactory => _campsDb.Value.Factory;

    private protected FakeClock Clock { get; }
    private protected IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    // xUnit builds a fresh class instance per test, so these substitutes never leak state.

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();
    private protected IAdminAuthorizationService AdminAuthorization { get; } = Substitute.For<IAdminAuthorizationService>();

    protected CampsTestHarness(Instant? now = null)
    {
        DbOptions = TestDbOptions.InMemory<UsersDbContext>();
        Db = new UsersDbContext(DbOptions);
        _dbs.Track(Db);

        _campsDb = _dbs.Add<CampsDbContext>(o => new(o));
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    /// <summary>
    /// Flushes <see cref="Db"/> and every touched section context, so a test seeding across
    /// several need not track which context each row landed in.
    /// </summary>
    private protected Task SaveAllAsync(CancellationToken ct = default) => _dbs.SaveAllAsync(ct);

    /// <summary>
    /// Clears the change tracker on <see cref="Db"/> and every peeled-section
    /// context. Tests do this after exercising a service so the read-back sees
    /// what the repository actually wrote rather than the instance the seed
    /// left tracked — the entity lives in whichever context owns its
    /// section, so clearing only <see cref="Db"/> silently returns stale state.
    /// </summary>
    private protected void ClearAllTrackers() => _dbs.ClearTrackers();

    /// <summary>Synchronous <see cref="SaveAllAsync"/>.</summary>
    private protected void SaveAll() => _dbs.SaveAll();

    public virtual void Dispose()
    {
        Cache.Dispose();
        _dbs.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>An <see cref="IUserService"/> substitute whose readers read this harness's in-memory DB.</summary>
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
            PreferredLanguage = "en"
        };
        LegacyDisplayNameProperty.SetValue(user, displayName);
        Db.Users.Add(user);
        return user;
    }

    protected User SeedUser(string displayName) => SeedUser(null, displayName);

}

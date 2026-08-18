// A trimmed COPY of Humans.Application.Tests' ServiceTestHarness, not a share.
// The section's four service/repository test classes make 127 CampsDb, 78 SeedSettingsAsync,
// 77 Clock and 46 SaveAllAsync calls — the whole harness, so Campaigns' "rewrite the stubs"
// does not scale and Governance's "split the helper" has nothing to split (Teams finding 37).
// Sharing it instead would push InternalsVisibleTo on Base's contexts into every test project
// compiling the shared set. The Teams / Shifts / GoogleIntegration section pairs and the team
// seeders are cut; Users stays because the camp tests seed owners.
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Notifications.Contracts;
using Humans.Shifts.Contracts;
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
/// Base class for service tests. Owns the per-test in-memory <see cref="UsersDbContext"/>,
/// an <see cref="IDbContextFactory{TContext}"/>, a deterministic <see cref="FakeClock"/>,
/// and an <see cref="IMemoryCache"/>, plus the most common entity seeders. Tests construct
/// their service-under-test in their own ctor using these resources; the harness does not
/// pre-build the service.
/// </summary>
public abstract class CampsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private protected DbContextOptions<UsersDbContext> DbOptions { get; }
    private protected UsersDbContext Db { get; }
    private protected TestDbContextFactory<UsersDbContext> DbFactory { get; }

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

    /// <summary>GoogleIntegration: <c>google_resources</c>, <c>google_sync_outbox</c>, <c>sync_service_settings</c>.</summary>



    /// <summary>Shifts: <c>event_settings</c>, <c>rotas</c>, <c>shifts</c>, <c>shift_signups</c>,
    /// <c>shift_tags</c>, <c>rota_shift_tags</c>, <c>volunteer_event_profiles</c>,
    /// <c>general_availability</c>, <c>volunteer_build_statuses</c>, <c>volunteer_tag_preferences</c>.</summary>


    /// <summary>Camps: <c>camps</c>, <c>camp_seasons</c>, <c>camp_historical_names</c>, <c>camp_images</c>, <c>camp_settings</c>, <c>camp_members</c>, <c>camp_role_definitions</c>, <c>camp_role_assignments</c>.</summary>
    private readonly Lazy<SectionDb<CampsDbContext>> _campsDb;
    private protected CampsDbContext CampsDb => _campsDb.Value.Context;
    private protected TestDbContextFactory<CampsDbContext> CampsDbFactory => _campsDb.Value.Factory;

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

    protected CampsTestHarness(Instant? now = null)
    {
        DbOptions = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new UsersDbContext(DbOptions);
        DbFactory = new TestDbContextFactory<UsersDbContext>(DbOptions);


        _campsDb = RegisterSection<CampsDbContext>(o => new(o));
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





}

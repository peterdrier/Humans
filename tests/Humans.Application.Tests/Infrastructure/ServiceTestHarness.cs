using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
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

    /// <summary>Auth: <c>role_assignments</c> (see <see cref="SeedRoleAssignment"/>).</summary>
    private readonly Lazy<SectionDb<AuthDbContext>> _authDb;
    private protected AuthDbContext AuthDb => _authDb.Value.Context;
    private protected TestDbContextFactory<AuthDbContext> AuthDbFactory => _authDb.Value.Factory;

    /// <summary>Governance: <c>applications</c>, <c>application_state_history</c>, <c>board_votes</c>.</summary>
    private readonly Lazy<SectionDb<GovernanceDbContext>> _governanceDb;
    private protected GovernanceDbContext GovernanceDb => _governanceDb.Value.Context;
    private protected TestDbContextFactory<GovernanceDbContext> GovernanceDbFactory => _governanceDb.Value.Factory;

    /// <summary>Campaigns: <c>campaigns</c>, <c>campaign_codes</c>, <c>campaign_grants</c>.</summary>
    private readonly Lazy<SectionDb<CampaignsDbContext>> _campaignsDb;
    private protected CampaignsDbContext CampaignsDb => _campaignsDb.Value.Context;
    private protected TestDbContextFactory<CampaignsDbContext> CampaignsDbFactory => _campaignsDb.Value.Factory;

    /// <summary>GoogleIntegration: <c>google_resources</c>, <c>google_sync_outbox</c>, <c>sync_service_settings</c>.</summary>
    private readonly Lazy<SectionDb<GoogleIntegrationDbContext>> _googleIntegrationDb;
    private protected GoogleIntegrationDbContext GoogleIntegrationDb => _googleIntegrationDb.Value.Context;
    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleIntegrationDbFactory => _googleIntegrationDb.Value.Factory;

    /// <summary>Tickets: <c>ticket_orders</c>, <c>ticket_attendees</c>, <c>ticket_sync_state</c>, <c>ticket_transfer_requests</c>.</summary>
    private readonly Lazy<SectionDb<TicketsDbContext>> _ticketsDb;
    private protected TicketsDbContext TicketsDb => _ticketsDb.Value.Context;
    private protected TestDbContextFactory<TicketsDbContext> TicketsDbFactory => _ticketsDb.Value.Factory;

    /// <summary>Feedback: <c>feedback_reports</c>, <c>feedback_messages</c>.</summary>
    private readonly Lazy<SectionDb<FeedbackDbContext>> _feedbackDb;
    private protected FeedbackDbContext FeedbackDb => _feedbackDb.Value.Context;
    private protected TestDbContextFactory<FeedbackDbContext> FeedbackDbFactory => _feedbackDb.Value.Factory;

    /// <summary>CityPlanning: <c>city_planning_settings</c>, <c>camp_polygons</c>, <c>camp_polygon_histories</c>.</summary>
    private readonly Lazy<SectionDb<CityPlanningDbContext>> _cityPlanningDb;
    private protected CityPlanningDbContext CityPlanningDb => _cityPlanningDb.Value.Context;
    private protected TestDbContextFactory<CityPlanningDbContext> CityPlanningDbFactory => _cityPlanningDb.Value.Factory;

    /// <summary>Budget: <c>budget_years</c>, <c>budget_groups</c>, <c>budget_categories</c>, <c>budget_line_items</c>, <c>budget_audit_logs</c>, <c>ticketing_projections</c>.</summary>
    private readonly Lazy<SectionDb<BudgetDbContext>> _budgetDb;
    private protected BudgetDbContext BudgetDb => _budgetDb.Value.Context;
    private protected TestDbContextFactory<BudgetDbContext> BudgetDbFactory => _budgetDb.Value.Factory;

    /// <summary>Camps: <c>camps</c>, <c>camp_seasons</c>, <c>camp_historical_names</c>, <c>camp_images</c>, <c>camp_settings</c>, <c>camp_members</c>, <c>camp_role_definitions</c>, <c>camp_role_assignments</c>.</summary>
    private readonly Lazy<SectionDb<CampsDbContext>> _campsDb;
    private protected CampsDbContext CampsDb => _campsDb.Value.Context;
    private protected TestDbContextFactory<CampsDbContext> CampsDbFactory => _campsDb.Value.Factory;

    /// <summary>Gate: <c>gate_scan_events</c>, <c>gate_settings</c>, <c>gate_staff_pins</c>.</summary>
    private readonly Lazy<SectionDb<GateDbContext>> _gateDb;
    private protected GateDbContext GateDb => _gateDb.Value.Context;
    private protected TestDbContextFactory<GateDbContext> GateDbFactory => _gateDb.Value.Factory;

    /// <summary>Legal: <c>legal_documents</c>, <c>document_versions</c>, <c>consent_records</c>.</summary>
    private readonly Lazy<SectionDb<LegalDbContext>> _legalDb;
    private protected LegalDbContext LegalDb => _legalDb.Value.Context;
    private protected TestDbContextFactory<LegalDbContext> LegalDbFactory => _legalDb.Value.Factory;

    /// <summary>AuditLog: <c>audit_log</c>.</summary>
    private readonly Lazy<SectionDb<AuditLogDbContext>> _auditLogDb;
    private protected AuditLogDbContext AuditLogDb => _auditLogDb.Value.Context;
    private protected TestDbContextFactory<AuditLogDbContext> AuditLogDbFactory => _auditLogDb.Value.Factory;

    /// <summary>Shifts: <c>event_settings</c>, <c>rotas</c>, <c>shifts</c>, <c>shift_signups</c>,
    /// <c>shift_tags</c>, <c>rota_shift_tags</c>, <c>volunteer_event_profiles</c>,
    /// <c>general_availability</c>, <c>volunteer_build_statuses</c>, <c>volunteer_tag_preferences</c>.</summary>
    private readonly Lazy<SectionDb<ShiftsDbContext>> _shiftsDb;
    private protected ShiftsDbContext ShiftsDb => _shiftsDb.Value.Context;
    private protected TestDbContextFactory<ShiftsDbContext> ShiftsDbFactory => _shiftsDb.Value.Factory;

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
        DbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new HumansDbContext(DbOptions);
        DbFactory = new TestDbContextFactory(DbOptions);

        _authDb = RegisterSection<AuthDbContext>(o => new(o));
        _governanceDb = RegisterSection<GovernanceDbContext>(o => new(o));
        _campaignsDb = RegisterSection<CampaignsDbContext>(o => new(o));
        _googleIntegrationDb = RegisterSection<GoogleIntegrationDbContext>(o => new(o));
        _ticketsDb = RegisterSection<TicketsDbContext>(o => new(o));
        _feedbackDb = RegisterSection<FeedbackDbContext>(o => new(o));
        _cityPlanningDb = RegisterSection<CityPlanningDbContext>(o => new(o));
        _budgetDb = RegisterSection<BudgetDbContext>(o => new(o));
        _campsDb = RegisterSection<CampsDbContext>(o => new(o));
        _gateDb = RegisterSection<GateDbContext>(o => new(o));
        _legalDb = RegisterSection<LegalDbContext>(o => new(o));
        _auditLogDb = RegisterSection<AuditLogDbContext>(o => new(o));
        _shiftsDb = RegisterSection<ShiftsDbContext>(o => new(o));

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
            UserId = userId,
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
        // Unlike the Db seeders above, this one saves: role_assignments sits in
        // its own context since the Auth peel, so callers' `Db.SaveChangesAsync()`
        // would never reach it, and the row has no ordering dependency on
        // anything staged in Db.
        AuthDb.RoleAssignments.Add(ra);
        AuthDb.SaveChanges();
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

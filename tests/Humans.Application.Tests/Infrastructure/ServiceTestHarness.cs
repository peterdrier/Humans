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

    /// <summary>Auth: <c>role_assignments</c> (see <see cref="SeedRoleAssignment"/>).</summary>
    private protected AuthDbContext AuthDb { get; }
    private protected TestDbContextFactory<AuthDbContext> AuthDbFactory { get; }

    /// <summary>Governance: <c>applications</c>, <c>application_state_history</c>, <c>board_votes</c>.</summary>
    private protected GovernanceDbContext GovernanceDb { get; }
    private protected TestDbContextFactory<GovernanceDbContext> GovernanceDbFactory { get; }

    /// <summary>Campaigns: <c>campaigns</c>, <c>campaign_codes</c>, <c>campaign_grants</c>.</summary>
    private protected CampaignsDbContext CampaignsDb { get; }
    private protected TestDbContextFactory<CampaignsDbContext> CampaignsDbFactory { get; }

    /// <summary>GoogleIntegration: <c>google_resources</c>, <c>google_sync_outbox</c>, <c>sync_service_settings</c>.</summary>
    private protected GoogleIntegrationDbContext GoogleIntegrationDb { get; }
    private protected TestDbContextFactory<GoogleIntegrationDbContext> GoogleIntegrationDbFactory { get; }

    /// <summary>Tickets: <c>ticket_orders</c>, <c>ticket_attendees</c>, <c>ticket_sync_state</c>, <c>ticket_transfer_requests</c>.</summary>
    private protected TicketsDbContext TicketsDb { get; }
    private protected TestDbContextFactory<TicketsDbContext> TicketsDbFactory { get; }

    /// <summary>Feedback: <c>feedback_reports</c>, <c>feedback_messages</c>.</summary>
    private protected FeedbackDbContext FeedbackDb { get; }
    private protected TestDbContextFactory<FeedbackDbContext> FeedbackDbFactory { get; }

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

        var authDbOptions = NewSectionDbOptions<AuthDbContext>();
        AuthDb = new AuthDbContext(authDbOptions);
        AuthDbFactory = new TestDbContextFactory<AuthDbContext>(authDbOptions);

        var governanceDbOptions = NewSectionDbOptions<GovernanceDbContext>();
        GovernanceDb = new GovernanceDbContext(governanceDbOptions);
        GovernanceDbFactory = new TestDbContextFactory<GovernanceDbContext>(governanceDbOptions);

        var campaignsDbOptions = NewSectionDbOptions<CampaignsDbContext>();
        CampaignsDb = new CampaignsDbContext(campaignsDbOptions);
        CampaignsDbFactory = new TestDbContextFactory<CampaignsDbContext>(campaignsDbOptions);

        var googleIntegrationDbOptions = NewSectionDbOptions<GoogleIntegrationDbContext>();
        GoogleIntegrationDb = new GoogleIntegrationDbContext(googleIntegrationDbOptions);
        GoogleIntegrationDbFactory = new TestDbContextFactory<GoogleIntegrationDbContext>(googleIntegrationDbOptions);

        var ticketsDbOptions = NewSectionDbOptions<TicketsDbContext>();
        TicketsDb = new TicketsDbContext(ticketsDbOptions);
        TicketsDbFactory = new TestDbContextFactory<TicketsDbContext>(ticketsDbOptions);

        var feedbackDbOptions = NewSectionDbOptions<FeedbackDbContext>();
        FeedbackDb = new FeedbackDbContext(feedbackDbOptions);
        FeedbackDbFactory = new TestDbContextFactory<FeedbackDbContext>(feedbackDbOptions);

        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

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
        await AuthDb.SaveChangesAsync(ct);
        await GovernanceDb.SaveChangesAsync(ct);
        await CampaignsDb.SaveChangesAsync(ct);
        await GoogleIntegrationDb.SaveChangesAsync(ct);
        await TicketsDb.SaveChangesAsync(ct);
        await FeedbackDb.SaveChangesAsync(ct);
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
        AuthDb.ChangeTracker.Clear();
        GovernanceDb.ChangeTracker.Clear();
        CampaignsDb.ChangeTracker.Clear();
        GoogleIntegrationDb.ChangeTracker.Clear();
        TicketsDb.ChangeTracker.Clear();
        FeedbackDb.ChangeTracker.Clear();
    }

    /// <summary>Synchronous <see cref="SaveAllAsync"/>.</summary>
    private protected void SaveAll()
    {
        Db.SaveChanges();
        AuthDb.SaveChanges();
        GovernanceDb.SaveChanges();
        CampaignsDb.SaveChanges();
        GoogleIntegrationDb.SaveChanges();
        TicketsDb.SaveChanges();
        FeedbackDb.SaveChanges();
    }

    public virtual void Dispose()
    {
        Cache.Dispose();
        Db.Dispose();
        AuthDb.Dispose();
        GovernanceDb.Dispose();
        CampaignsDb.Dispose();
        GoogleIntegrationDb.Dispose();
        TicketsDb.Dispose();
        FeedbackDb.Dispose();
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

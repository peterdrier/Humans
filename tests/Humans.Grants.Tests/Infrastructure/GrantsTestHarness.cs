using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Grants.Data;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Grants.Tests.Infrastructure;

/// <summary>
/// Base class for the section's tests: a per-test in-memory <see cref="GrantsDbContext"/> and
/// factory, a deterministic clock, substitutes for every cross-section seam the service talks
/// to, and seeders for programs and grants. Member names follow the other section harnesses
/// (memory/code/service-test-harness.md): <c>Db</c>, <c>DbFactory</c>, <c>Clock</c>, <c>SeedUser</c>.
/// </summary>
public abstract class GrantsTestHarness : IDisposable
{
    /// <summary>The harness "today" in Europe/Madrid: 2027-03-01.</summary>
    protected static readonly LocalDate Today = new(2027, 3, 1);

    private readonly Dictionary<Guid, UserInfo> _users = [];
    private bool _disposed;

    protected GrantsTestHarness()
    {
        var options = new DbContextOptionsBuilder<GrantsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new GrantsDbContext(options);
        DbFactory = new TestDbContextFactory<GrantsDbContext>(options);
        Clock = new FakeClock(Instant.FromUtc(2027, 3, 1, 12, 0));

        Users = Substitute.For<IUserService>();
        Users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(_users.GetValueOrDefault(call.Arg<Guid>())));

        UserEmails = Substitute.For<IUserEmailService>();
        UserEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call.Args()[0]!;
                IReadOnlyDictionary<Guid, string> dict = ids.Distinct()
                    .Where(_users.ContainsKey)
                    .ToDictionary(id => id, id => $"{id}@example.org");
                return Task.FromResult(dict);
            });

        Notifications = Substitute.For<INotificationEmitter>();
        Email = Substitute.For<IEmailService>();
        EmailFactory = TestGrantsEmails.Create();
        AuditLog = Substitute.For<IAuditLogService>();
        Logger = new CapturingLogger<GrantService>();
    }

    private protected GrantsDbContext Db { get; }
    private protected TestDbContextFactory<GrantsDbContext> DbFactory { get; }
    private protected FakeClock Clock { get; }
    private protected IUserService Users { get; }
    private protected IUserEmailService UserEmails { get; }
    private protected INotificationEmitter Notifications { get; }
    private protected IEmailService Email { get; }
    private protected GrantsEmails EmailFactory { get; }
    private protected IAuditLogService AuditLog { get; }
    private protected CapturingLogger<GrantService> Logger { get; }

    private protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The service over the real repository and the substitutes above.</summary>
    private protected GrantService NewService() => new(
        new GrantRepository(DbFactory), Users, UserEmails, Notifications, Email, EmailFactory, AuditLog, Clock, Logger);

    /// <summary>A fresh context over the same store — what a test reads back through.</summary>
    private protected GrantsDbContext OpenContext() => DbFactory.CreateDbContext();

    /// <summary>Moves the clock so that "today" in Madrid is <paramref name="date"/>.</summary>
    private protected void SetToday(LocalDate date) => Clock.Reset(date.AtMidnight().InUtc().ToInstant().Plus(Duration.FromHours(12)));

    // ── Seeders ───────────────────────────────────────────────────────────

    /// <summary>Registers a human the <see cref="IUserService"/> substitute knows by burner name.</summary>
    protected Guid SeedUser(string burnerName = "Test Human", Guid? id = null, string language = "en", IReadOnlyList<Guid>? mergedIds = null)
    {
        var userId = id ?? Guid.NewGuid();
        _users[userId] = new UserInfo(
            userId, burnerName, false, language, null, Instant.FromUtc(2026, 1, 1, 0, 0),
            null, null, null, null, null, null, null,
            null, null, null, [], [], [],
            UserFixtures.Profile(burnerName: burnerName, membershipTier: LegacyMembershipTier.Volunteer), [])
        { MergedUserIds = mergedIds ?? [] };
        return userId;
    }

    private protected async Task<GrantProgram> SeedProgramAsync(
        GrantIssuer issuedBy = GrantIssuer.Managers,
        string? sourceKey = null,
        LocalDate? expiresOn = null,
        GrantProgramStatus status = GrantProgramStatus.Open,
        string name = "Build crew 2027",
        params Guid[] managers)
    {
        var now = Clock.GetCurrentInstant();
        var program = new GrantProgram
        {
            Id = Guid.NewGuid(),
            Name = name,
            Entitles = "a ticket at the build-crew price",
            Year = 2027,
            IssuedBy = issuedBy,
            SourceKey = issuedBy == GrantIssuer.Source ? sourceKey ?? "low-income" : null,
            GrantsExpireOn = expiresOn,
            Status = status,
            CreatedByUserId = SeedUser("Creator"),
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var manager in managers)
            program.Managers.Add(new GrantProgramManager { ProgramId = program.Id, UserId = manager, AddedByUserId = program.CreatedByUserId, AddedAt = now });
        Db.Programs.Add(program);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        return program;
    }

    private protected async Task<Grant> SeedGrantAsync(
        GrantProgram program, Guid? userId = null, LocalDate? expiresOn = null, bool revoked = false)
    {
        var now = Clock.GetCurrentInstant();
        var grant = new Grant
        {
            Id = Guid.NewGuid(),
            ProgramId = program.Id,
            UserId = userId ?? SeedUser("Holder"),
            ExpiresOn = expiresOn,
            IssuedAt = now,
            IssuedByUserId = program.CreatedByUserId,
            NotifiedAt = now,
            RevokedAt = revoked ? now : null,
            RevokedByUserId = revoked ? program.CreatedByUserId : null
        };
        Db.Grants.Add(grant);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        return grant;
    }

    private protected async Task<Grant> ReadGrantAsync(Guid id)
    {
        await using var db = OpenContext();
        return await db.Grants.AsNoTracking().SingleAsync(g => g.Id == id, Ct);
    }

    private protected async Task<List<Grant>> ReadGrantsAsync()
    {
        await using var db = OpenContext();
        return await db.Grants.AsNoTracking().ToListAsync(Ct);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) Db.Dispose();
        _disposed = true;
    }
}

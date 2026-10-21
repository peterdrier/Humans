using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Data;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Infrastructure;

/// <summary>
/// Base class for the section's tests: a per-test in-memory <see cref="PreapprovalsDbContext"/>
/// and factory, a deterministic clock, substitutes for every cross-section seam the service
/// talks to, and seeders for programs and grants. Member names follow the other section
/// harnesses (memory/code/service-test-harness.md): <c>Db</c>, <c>DbFactory</c>, <c>Clock</c>, <c>SeedUser</c>.
/// </summary>
public abstract class PreapprovalsTestHarness : IDisposable
{
    /// <summary>The harness "today" in Europe/Madrid: 2026-03-01.</summary>
    protected static readonly LocalDate Today = new(2026, 3, 1);

    private readonly Dictionary<Guid, UserInfo> _users = [];
    private bool _disposed;

    protected PreapprovalsTestHarness()
    {
        var options = new DbContextOptionsBuilder<PreapprovalsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new PreapprovalsDbContext(options);
        DbFactory = new TestDbContextFactory<PreapprovalsDbContext>(options);
        Clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        Users = Substitute.For<IUserService>();
        Users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(_users.GetValueOrDefault(call.Arg<Guid>())));
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids.Distinct()
                    .Where(_users.ContainsKey)
                    .ToDictionary(id => id, id => _users[id]);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

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

        Finance = Substitute.For<IHoldedFinanceService>();
        Finance.ListExpenseAccountsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<HoldedExpenseAccountOption>>(
            [
                new HoldedExpenseAccountOption(62900150, "acc-62900150", "Creativity grants", true, true),
                new HoldedExpenseAccountOption(62900160, "acc-62900160", "Infrastructure", true, true)
            ]));

        Notifications = Substitute.For<INotificationEmitter>();
        NotificationResolve = Substitute.For<INotificationAutoResolve>();
        Email = Substitute.For<IEmailService>();
        EmailFactory = TestPreapprovalsEmails.Create();
        AuditLog = Substitute.For<IAuditLogService>();
        Logger = new CapturingLogger<PreapprovalService>();
    }

    private protected PreapprovalsDbContext Db { get; }
    private protected TestDbContextFactory<PreapprovalsDbContext> DbFactory { get; }
    private protected FakeClock Clock { get; }
    private protected IUserService Users { get; }
    private protected IUserEmailService UserEmails { get; }
    private protected IHoldedFinanceService Finance { get; }
    private protected INotificationEmitter Notifications { get; }
    private protected INotificationAutoResolve NotificationResolve { get; }
    private protected IEmailService Email { get; }
    private protected PreapprovalsEmails EmailFactory { get; }
    private protected IAuditLogService AuditLog { get; }
    private protected CapturingLogger<PreapprovalService> Logger { get; }

    private protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The service over the real repository and the substitutes above.</summary>
    private protected PreapprovalService NewService(PreapprovalsEmails? emailFactory = null) => new(
        new PreapprovalRepository(DbFactory), Users, UserEmails, Finance, Notifications, NotificationResolve,
        Email, emailFactory ?? EmailFactory, AuditLog, Clock, Logger);

    /// <summary>A fresh context over the same store — what a test reads back through.</summary>
    private protected PreapprovalsDbContext OpenContext() => DbFactory.CreateDbContext();

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

    private protected async Task<PreapprovalProgram> SeedProgramAsync(
        ProgramKind kind = ProgramKind.FixedPerPerson,
        decimal? perPerson = 300m,
        decimal? ceiling = 1000m,
        LocalDate? claimBy = null,
        ProgramStatus status = ProgramStatus.Open,
        bool acceptsApplications = true,
        string name = "2026 Creativity",
        params Guid[] managers)
    {
        var now = Clock.GetCurrentInstant();
        var program = new PreapprovalProgram
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            PerPersonAmount = kind == ProgramKind.FixedPerPerson ? perPerson : null,
            TotalCeiling = ceiling,
            HoldedAccountNumber = 62900150,
            HoldedAccountId = "acc-62900150",
            AcceptsApplications = acceptsApplications,
            ClaimBy = claimBy ?? Today.PlusMonths(3),
            Status = status,
            CreatedByUserId = SeedUser("Creator"),
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var manager in managers)
            program.Managers.Add(new ProgramManager { ProgramId = program.Id, UserId = manager, AddedByUserId = program.CreatedByUserId, AddedAt = now });
        Db.Programs.Add(program);
        await Db.SaveChangesAsync(Ct);
        return program;
    }

    private protected async Task<PreapprovalGrant> SeedGrantAsync(
        PreapprovalProgram program, Guid? beneficiary = null, decimal? amount = null,
        GrantStatus status = GrantStatus.Open, Guid? expenseReportId = null, string? label = null,
        decimal? requestedAmount = null)
    {
        var now = Clock.GetCurrentInstant();
        var grant = new PreapprovalGrant
        {
            Id = Guid.NewGuid(),
            ProgramId = program.Id,
            BeneficiaryUserId = beneficiary ?? SeedUser("Beneficiary"),
            Label = label,
            MaxAmount = amount ?? program.PerPersonAmount ?? 100m,
            Status = status,
            RequestedAmount = requestedAmount,
            ExpenseReportId = expenseReportId,
            DecidedByUserId = status == GrantStatus.Applied ? null : program.CreatedByUserId,
            DecidedAt = status == GrantStatus.Applied ? null : now,
            CreatedAt = now,
            UpdatedAt = now
        };
        Db.Grants.Add(grant);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        return grant;
    }

    private protected async Task<PreapprovalGrant> ReadGrantAsync(Guid id)
    {
        await using var db = OpenContext();
        return await db.Grants.AsNoTracking().SingleAsync(g => g.Id == id, Ct);
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

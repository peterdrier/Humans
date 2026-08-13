using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.AuditLog.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Domain;
using Humans.Domain.Entities;
using Humans.Notifications.Contracts;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Auth.Tests.Infrastructure;

/// <summary>
/// Base class for the section's service tests: the in-memory <see cref="AuthDbContext"/>
/// and its factory, a deterministic clock, the two substituted horizontal collaborators,
/// and a dictionary-backed <see cref="IUserServiceRead"/>.
/// </summary>
/// <remarks>
/// Campaigns' shape rather than Teams'. Teams copied <c>ServiceTestHarness</c> wholesale
/// because 5,000 lines of test bodies read <c>Db.Teams</c>/<c>Db.Users</c>; Auth's service
/// reads exactly two members of <c>IUserServiceRead</c> — <c>GetUserInfoAsync</c> and
/// <c>GetUserInfosAsync</c>, both for display-name stitching (design-rules §6b) — so
/// rewriting the *stub* keeps the test bodies unchanged and keeps
/// <c>HumansDbContext</c> off a section test project's compile path entirely. That matters
/// beyond tidiness: §858 peel 15 deletes <c>HumansDbContext</c>, and copying the harness
/// would have taken a dependency on it in the same week.
/// <c>UserInfo.Create</c> is Governance's <c>UserInfoFixtures</c> projection.
/// </remarks>
public abstract class AuthTestHarness : IDisposable
{
    private readonly Dictionary<Guid, UserInfo> _users = [];

    private protected AuthDbContext AuthDb { get; }
    private protected TestDbContextFactory<AuthDbContext> AuthDbFactory { get; }
    private protected FakeClock Clock { get; }

    private protected IAuditLogService AuditLog { get; } = Substitute.For<IAuditLogService>();
    private protected INotificationEmitter Notifier { get; } = Substitute.For<INotificationEmitter>();

    protected AuthTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        AuthDb = new AuthDbContext(options);
        AuthDbFactory = new TestDbContextFactory<AuthDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 1, 1, 0, 0));
    }

    /// <summary>
    /// Registers a person the service can stitch a display name for. Returns the
    /// <see cref="User"/> so call sites that used the shared harness's seeder read the same.
    /// </summary>
    private protected User SeedUser(Guid? id = null, string displayName = "Test User")
    {
        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            DisplayName = displayName,
            UserName = $"test-{id ?? Guid.NewGuid()}@test.com",
            Email = $"test-{id ?? Guid.NewGuid()}@test.com",
            PreferredLanguage = "en"
        };
        _users[user.Id] = UserInfo.Create(user, [], [], [], profile: null, [], [], [], []);
        return user;
    }

    private protected RoleAssignment SeedRoleAssignment(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        Guid? createdByUserId = null)
    {
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            CreatedAt = Clock.GetCurrentInstant(),
            CreatedByUserId = createdByUserId ?? Guid.NewGuid()
        };
        AuthDb.RoleAssignments.Add(assignment);
        AuthDb.SaveChanges();
        return assignment;
    }

    private protected IUserServiceRead NewStubUserService()
    {
        var svc = Substitute.For<IUserServiceRead>();

        svc.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(
                _users.TryGetValue(call.Arg<Guid>(), out var info) ? info : null));

        svc.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                call.Arg<IReadOnlyCollection<Guid>>()
                    .Distinct()
                    .Where(_users.ContainsKey)
                    .ToDictionary(id => id, id => _users[id])));

        svc.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>(_users.Values.ToList()));

        return svc;
    }

    public void Dispose()
    {
        AuthDb.Dispose();
        GC.SuppressFinalize(this);
    }
}

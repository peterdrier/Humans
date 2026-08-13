using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Tickets.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Tickets.Tests;

/// <summary>
/// Base class for the section's service tests: the per-test in-memory
/// <see cref="TicketsDbContext"/> and factory, a deterministic clock, and an in-memory
/// people registry standing in for the Users tables the section cannot see.
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests</c>' <c>ServiceTestHarness</c>, which is built
/// around an in-memory <c>HumansDbContext</c> a section test project has no business
/// seeing (G5-SECTION-TEMPLATE.md step 8). The member names are deliberately the
/// harness's — <c>Db</c>, <c>SeedUser</c>, <c>NewDbBackedUserService</c>,
/// <c>SaveAllAsync</c>, <c>Clock</c> — so the test bodies moved across unchanged
/// (Campaigns' "rewrite the stub, not the tests", Issues' naming trick).
/// </remarks>
public abstract class TicketsTestHarness : IDisposable
{
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private bool _disposed;

    protected TicketsTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<TicketsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        TicketsDb = new TicketsDbContext(options);
        TicketsDbFactory = new TestDbContextFactory<TicketsDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));
    }

    private protected TicketsDbContext TicketsDb { get; }
    private protected TestDbContextFactory<TicketsDbContext> TicketsDbFactory { get; }
    private protected FakeClock Clock { get; }

    /// <summary>
    /// The people the section's email matching resolves against. Named <c>Db</c> because
    /// that is what the moved test bodies call it; it is a pair of lists, not a context.
    /// </summary>
    private protected PeopleRegistry Db { get; } = new();

    /// <summary>Stages a user. Mirrors the Base harness: no save until <see cref="SaveAllAsync"/>.</summary>
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

    private protected Task SaveAllAsync(CancellationToken ct = default) => TicketsDb.SaveChangesAsync(ct);

    private protected void SaveAll() => TicketsDb.SaveChanges();

    /// <summary>
    /// An <see cref="IUserService"/> whose three <c>GetUserInfo*</c> readers project out of
    /// <see cref="Db"/>. Same three stubs the Base harness wires from <c>HumansDbContext</c>.
    /// </summary>
    private protected IUserService NewDbBackedUserService()
    {
        var svc = Substitute.For<IUserService>();
        StubUserInfos(svc);
        return svc;
    }

    /// <summary>Wires the three <c>GetUserInfo*</c> readers on an existing substitute.</summary>
    private protected IUserService StubUserInfos(IUserService userService)
    {
        userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(
                Db.Users.FirstOrDefault(u => u.Id == call.Arg<Guid>()) is { } u ? UserInfoFor(u) : null));

        userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids.Count == 0
                    ? new Dictionary<Guid, UserInfo>()
                    : Db.Users.Where(u => ids.Contains(u.Id)).ToDictionary(u => u.Id, UserInfoFor);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>(
                Db.Users.Select(UserInfoFor).ToList()));

        return userService;
    }

    private UserInfo UserInfoFor(User user) =>
        user.ToUserInfo(Db.UserEmails.Where(e => e.UserId == user.Id).ToList());

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) TicketsDb.Dispose();
        _disposed = true;
    }

    /// <summary>The users and user emails the section resolves ticket buyers against.</summary>
    public sealed class PeopleRegistry
    {
        public List<User> Users { get; } = [];
        public List<UserEmail> UserEmails { get; } = [];
    }
}

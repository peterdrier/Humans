using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Domain.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using NotificationMeterProvider = Humans.Application.Services.Notifications.NotificationMeterProvider;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Covers the push-model inversion from issue nobodies-collective/Humans#581:
/// <see cref="NotificationMeterProvider"/> discovers meters via registered
/// <see cref="INotificationMeterContributor"/>s, filters by per-user role
/// visibility, caches the global bundle for ~2 minutes, and isolates contributor
/// failures so one section's exception cannot suppress another section's meter.
/// </summary>
public class NotificationMeterProviderTests : IDisposable
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetMetersForUserAsync_Empty_WhenNoContributorsRegistered()
    {
        var provider = CreateProvider([]);
        var meters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));
        meters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMetersForUserAsync_RespectsVisibility()
    {
        var admin = new StubContributor("admin-only", NotificationMeterScope.Global,
            p => p.IsInRole(RoleNames.Admin),
            new NotificationMeter { Title = "Admin meter", Count = 1, ActionUrl = "/a", Priority = 1 });
        var board = new StubContributor("board-only", NotificationMeterScope.Global,
            p => p.IsInRole(RoleNames.Board),
            new NotificationMeter { Title = "Board meter", Count = 2, ActionUrl = "/b", Priority = 2 });

        var provider = CreateProvider([admin, board]);

        var adminMeters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));
        adminMeters.Should().ContainSingle().Which.Title.Should().Be("Admin meter");

        var boardMeters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board));
        boardMeters.Should().ContainSingle().Which.Title.Should().Be("Board meter");

        var otherMeters = await provider.GetMetersForUserAsync(CreatePrincipal("Volunteer"));
        otherMeters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMetersForUserAsync_OmitsMetersReturningNull()
    {
        var visible = new StubContributor("v", NotificationMeterScope.Global,
            _ => true,
            new NotificationMeter { Title = "Keep", Count = 1, ActionUrl = "/k", Priority = 1 });
        var nullMeter = new StubContributor("n", NotificationMeterScope.Global, _ => true, meter: null);

        var provider = CreateProvider([visible, nullMeter]);
        var meters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

        meters.Should().ContainSingle().Which.Title.Should().Be("Keep");
    }

    [Fact]
    public async Task GetMetersForUserAsync_FailingContributor_IsolatedFromOthers()
    {
        var good = new StubContributor("good", NotificationMeterScope.Global,
            _ => true,
            new NotificationMeter { Title = "Good", Count = 1, ActionUrl = "/g", Priority = 1 });
        var bad = new StubContributor("bad", NotificationMeterScope.Global,
            _ => true,
            new InvalidOperationException("boom"));

        var provider = CreateProvider([good, bad]);
        var meters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

        meters.Should().ContainSingle().Which.Title.Should().Be("Good");
    }

    [Fact]
    public async Task GetMetersForUserAsync_FailingPerUserContributor_IsolatedFromOthers()
    {
        var badPerUser = new StubContributor("bad-user", NotificationMeterScope.PerUser,
            _ => true,
            new InvalidOperationException("boom"));
        var goodGlobal = new StubContributor("good-global", NotificationMeterScope.Global,
            _ => true,
            new NotificationMeter { Title = "Good", Count = 1, ActionUrl = "/g", Priority = 1 });

        var provider = CreateProvider([badPerUser, goodGlobal]);
        var meters = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

        meters.Should().ContainSingle().Which.Title.Should().Be("Good");
    }

    [Fact]
    public async Task GetMetersForUserAsync_GlobalBundleCached_Within2Minutes()
    {
        var contributor = new StubContributor("c", NotificationMeterScope.Global,
            _ => true,
            new NotificationMeter { Title = "First", Count = 1, ActionUrl = "/x", Priority = 1 });

        var provider = CreateProvider([contributor]);

        var first = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));
        first.Should().ContainSingle().Which.Title.Should().Be("First");

        // Flip the contributor's meter; without invalidation the cached bundle
        // should still return the first value.
        contributor.Meter = new NotificationMeter
        { Title = "Second", Count = 2, ActionUrl = "/y", Priority = 1 };

        var second = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));
        second.Should().ContainSingle().Which.Title.Should().Be("First");

        contributor.InvocationCount.Should().Be(1, "global bundle is cached for 2 minutes");
    }

    [Fact]
    public async Task GetMetersForUserAsync_GlobalBundle_InvalidatedBySharedInvalidator()
    {
        var contributor = new StubContributor("c", NotificationMeterScope.Global,
            _ => true,
            new NotificationMeter { Title = "First", Count = 1, ActionUrl = "/x", Priority = 1 });

        var provider = CreateProvider([contributor]);
        await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));

        // Equivalent to INotificationMeterCacheInvalidator.Invalidate() — section
        // writes clear the shared CacheKeys.NotificationMeters entry.
        _cache.Remove(CacheKeys.NotificationMeters);

        contributor.Meter = new NotificationMeter
        { Title = "Second", Count = 2, ActionUrl = "/y", Priority = 1 };

        var after = await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Admin));
        after.Should().ContainSingle().Which.Title.Should().Be("Second");
        contributor.InvocationCount.Should().Be(2);
    }

    [Fact]
    public async Task GetMetersForUserAsync_PerUser_InvokedEveryRequest()
    {
        var contributor = new StubContributor("per-user", NotificationMeterScope.PerUser,
            _ => true,
            new NotificationMeter { Title = "Per user", Count = 1, ActionUrl = "/u", Priority = 1 });

        var provider = CreateProvider([contributor]);

        await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board));
        await provider.GetMetersForUserAsync(CreatePrincipal(RoleNames.Board));

        contributor.InvocationCount.Should().Be(2, "per-user contributors self-cache; provider does not share across calls");
    }

    [Fact]
    public async Task GetMetersForUserAsync_InvisibleContributor_NotInvoked()
    {
        var invisible = new StubContributor("invisible", NotificationMeterScope.Global,
            p => p.IsInRole(RoleNames.Board),
            new NotificationMeter { Title = "Never", Count = 1, ActionUrl = "/z", Priority = 1 });

        var provider = CreateProvider([invisible]);
        var meters = await provider.GetMetersForUserAsync(CreatePrincipal("Volunteer"));

        meters.Should().BeEmpty();
        invisible.InvocationCount.Should().Be(0,
            "not-visible contributors are filtered before the cache/build path runs");
    }

    private NotificationMeterProvider CreateProvider(IEnumerable<INotificationMeterContributor> contributors)
        => new(contributors, _cache, NullLogger<NotificationMeterProvider>.Instance);

    private static ClaimsPrincipal CreatePrincipal(params string[] roles)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private sealed class StubContributor : INotificationMeterContributor
    {
        private readonly Func<ClaimsPrincipal, bool> _visibility;
        private readonly Exception? _throw;

        public StubContributor(string key, NotificationMeterScope scope,
            Func<ClaimsPrincipal, bool> visibility, NotificationMeter? meter)
        {
            Key = key;
            Scope = scope;
            _visibility = visibility;
            Meter = meter;
        }

        public StubContributor(string key, NotificationMeterScope scope,
            Func<ClaimsPrincipal, bool> visibility, Exception toThrow)
        {
            Key = key;
            Scope = scope;
            _visibility = visibility;
            _throw = toThrow;
        }

        public string Key { get; }
        public NotificationMeterScope Scope { get; }
        public NotificationMeter? Meter { get; set; }
        public int InvocationCount { get; private set; }

        public bool IsVisibleTo(ClaimsPrincipal user) => _visibility(user);

        public Task<NotificationMeter?> BuildMeterAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            InvocationCount++;
            if (_throw is not null)
                throw _throw;
            return Task.FromResult(Meter);
        }
    }
}

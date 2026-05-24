using AwesomeAssertions;
using Humans.Application.Interfaces.Tickets;
using Humans.Infrastructure.Services.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Tickets;

public sealed class CachingTicketQueryServiceTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private readonly ITicketQueryService _inner;
    private readonly MemoryCache _perUserCache;
    private readonly CachingTicketQueryService _decorator;

    public CachingTicketQueryServiceTests()
    {
        _inner = Substitute.For<ITicketQueryService>();
        _perUserCache = new MemoryCache(new MemoryCacheOptions());

        var services = new ServiceCollection();
        services.AddKeyedScoped<ITicketQueryService>(
            CachingTicketQueryService.InnerServiceKey,
            (_, _) => _inner);

        _decorator = new CachingTicketQueryService(
            _perUserCache,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [HumansFact]
    public async Task GetUserTicketCountAsync_CachesInnerResult()
    {
        _inner.GetUserTicketCountAsync(UserA).Returns(2);

        var first = await _decorator.GetUserTicketCountAsync(UserA);
        var second = await _decorator.GetUserTicketCountAsync(UserA);

        first.Should().Be(2);
        second.Should().Be(2);
        await _inner.Received(1).GetUserTicketCountAsync(UserA);
    }

    [HumansFact]
    public async Task GetUserTicketHoldingsAsync_CachesInnerResult()
    {
        var holdings = new UserTicketHoldings(1, []);
        _inner.GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>())
            .Returns(holdings);

        var first = await _decorator.GetUserTicketHoldingsAsync(UserA);
        var second = await _decorator.GetUserTicketHoldingsAsync(UserA);

        first.Should().BeSameAs(holdings);
        second.Should().BeSameAs(holdings);
        await _inner.Received(1).GetUserTicketHoldingsAsync(UserA, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAfterTransfer_DropsAffectedPerUserEntries()
    {
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 5);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 0);
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserA), new UserTicketHoldings(1, []));
        _perUserCache.Set(CacheKeys.UserTicketHoldings(UserB), new UserTicketHoldings(0, []));

        _decorator.InvalidateAfterTransfer(UserA, UserB);

        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketHoldings(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public void InvalidateAfterUserMerge_DropsBothUsersPerUserEntries()
    {
        _perUserCache.Set(CacheKeys.UserTicketCount(UserA), 3);
        _perUserCache.Set(CacheKeys.UserTicketCount(UserB), 1);

        _decorator.InvalidateAfterUserMerge(UserA, UserB);

        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserA), out _).Should().BeFalse();
        _perUserCache.TryGetValue(CacheKeys.UserTicketCount(UserB), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task PassThroughRead_DelegatesToInner()
    {
        var expected = Instant.FromUtc(2026, 5, 1, 0, 0);
        _inner.GetPostEventHoldDateAsync(Arg.Any<CancellationToken>())
            .Returns(expected);

        var actual = await _decorator.GetPostEventHoldDateAsync();

        actual.Should().Be(expected);
        await _inner.Received(1).GetPostEventHoldDateAsync(Arg.Any<CancellationToken>());
    }
}

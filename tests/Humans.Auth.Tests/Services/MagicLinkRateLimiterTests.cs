using AwesomeAssertions;
using Humans.Auth.Services;
using Microsoft.Extensions.Caching.Memory;

// The subject is internal to Humans.Auth; this project reaches it through that
// assembly's InternalsVisibleTo. Run against a real MemoryCache rather than a
// substitute — what is worth pinning is that consume and release agree on the key.
namespace Humans.Auth.Tests.Services;

public sealed class MagicLinkRateLimiterTests : IDisposable
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MagicLinkRateLimiter _limiter;

    public MagicLinkRateLimiterTests() => _limiter = new MagicLinkRateLimiter(_cache);

    [HumansFact]
    public async Task TryConsumeTokenAsync_SecondAttempt_IsRefused()
    {
        (await _limiter.TryConsumeTokenAsync("token-abc", TokenLifetime)).Should().BeTrue();

        (await _limiter.TryConsumeTokenAsync("token-abc", TokenLifetime)).Should().BeFalse();
    }

    [HumansFact]
    public async Task ReleaseTokenReservation_MakesTheTokenRedeemableAgain()
    {
        // The failed-provisioning path: the redemption accomplished nothing, so the
        // link has to keep working for the rest of its 15 minutes.
        await _limiter.TryConsumeTokenAsync("token-abc", TokenLifetime);

        _limiter.ReleaseTokenReservation("token-abc");

        (await _limiter.TryConsumeTokenAsync("token-abc", TokenLifetime)).Should().BeTrue();
    }

    [HumansFact]
    public void ReleaseTokenReservation_UnreservedToken_IsANoOp()
    {
        var release = () => _limiter.ReleaseTokenReservation("never-seen");

        release.Should().NotThrow();
    }

    [HumansFact]
    public async Task TryConsumeTokenAsync_TokensSharingNoPrefix_DoNotCollide()
    {
        // Keys are built from a 32-char prefix, so two tokens are only distinct
        // to this limiter if they differ inside it.
        await _limiter.TryConsumeTokenAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-one", TokenLifetime);

        (await _limiter.TryConsumeTokenAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-two", TokenLifetime))
            .Should().BeTrue();
    }

    public void Dispose() => _cache.Dispose();
}

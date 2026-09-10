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
    public async Task TryConsumeTokenAsync_ConcurrentRedemptions_ExactlyOneWins()
    {
        // Single-use has to hold against two POSTs arriving together, not just against
        // a replay minutes later. IMemoryCache has no atomic add, so the reservation is
        // only exclusive because TryReserveAsync takes read and write under one lock —
        // remove that and several racers can each believe they created the entry.
        //
        // Honest about what this catches: measured against the non-atomic implementation
        // it goes red in roughly two runs out of three, and neither more racers nor more
        // rounds moved that much — a 4-core box only opens the window so often. So it is
        // a partial guard, not a proof. It cannot fail the other way (with the lock there
        // is exactly one winner every round), so it is never flaky, and it states the
        // invariant next to the code that has to keep it.
        const int Rounds = 25;
        const int Racers = 64;

        var winnersPerRound = new List<int>(Rounds);

        for (var round = 0; round < Rounds; round++)
        {
            var token = $"token-{round}";
            using var start = new ManualResetEventSlim(false);

            var racers = Enumerable.Range(0, Racers).Select(_ => Task.Run(() =>
            {
                start.Wait();
                return _limiter.TryConsumeTokenAsync(token, TokenLifetime);
            })).ToArray();

            start.Set();
            var outcomes = await Task.WhenAll(racers);
            winnersPerRound.Add(outcomes.Count(won => won));
        }

        winnersPerRound.Should().AllSatisfy(winners => winners.Should().Be(1));
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

using AwesomeAssertions;
using Humans.Auth.Services;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

// Runs against a real MemoryCache rather than a substitute: what is worth pinning is that
// consume and release agree on the key.
namespace Humans.Auth.Tests.Services;

public sealed class SignInLinkRateLimiterTests : IDisposable
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SignInLinkRateLimiter _limiter;

    public SignInLinkRateLimiterTests() => _limiter = new SignInLinkRateLimiter(_cache);

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
        // Single-use must hold against two simultaneous POSTs. IMemoryCache has no atomic add, so
        // only TryReserveAsync's lock makes the reservation exclusive. A partial guard: without the
        // lock it goes red only on some runs; with it there is one winner every round, never flaky.
        const int roundCount = 25;
        const int racerCount = 64;

        var winnersPerRound = new List<int>(roundCount);

        for (var round = 0; round < roundCount; round++)
        {
            var token = $"token-{round}";
            using var start = new ManualResetEventSlim(false);

            var racers = Enumerable.Range(0, racerCount).Select(_ => Task.Run(() =>
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
    public async Task SharedPrefixTokens_RedeemIndependently()
    {
        const string first = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-one";
        const string second = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-two";
        (await _limiter.TryConsumeTokenAsync(first, TokenLifetime)).Should().BeTrue();
        (await _limiter.TryConsumeTokenAsync(second, TokenLifetime)).Should().BeTrue();

        _limiter.ReleaseTokenReservation(first);

        (await _limiter.TryConsumeTokenAsync(first, TokenLifetime)).Should().BeTrue();
        (await _limiter.TryConsumeTokenAsync(second, TokenLifetime)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UserSendReservations_AreExclusivePerUser()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cooldown = TimeSpan.FromSeconds(60);
        (await _limiter.ReserveUserSendAsync(first, cooldown)).Should().Be(0);
        (await _limiter.ReserveUserSendAsync(first, cooldown)).Should().BePositive();
        (await _limiter.ReserveUserSendAsync(second, cooldown)).Should().Be(0);

        _limiter.ReleaseUserSendReservation(first);

        (await _limiter.ReserveUserSendAsync(first, cooldown)).Should().Be(0);
        (await _limiter.ReserveUserSendAsync(second, cooldown)).Should().BePositive();
    }

    [HumansTheory]
    [InlineData("first.last@gmail.com", "firstlast@gmail.com")]
    [InlineData("firstlast+signup@gmail.com", "FIRSTLAST@googlemail.com")]
    [InlineData("Member@example.org", "member@example.org")]
    public async Task SignupSendReservations_ShareInboxCooldown(string first, string alias)
    {
        var cooldown = TimeSpan.FromSeconds(60);
        (await _limiter.ReserveSignupSendAsync(first, cooldown)).Should().Be(0);
        (await _limiter.ReserveSignupSendAsync(alias, cooldown)).Should().BePositive();
        (await _limiter.ReserveSignupSendAsync("another@gmail.com", cooldown)).Should().Be(0);

        _limiter.ReleaseSignupReservation(alias);

        (await _limiter.ReserveSignupSendAsync(first, cooldown)).Should().Be(0);
        (await _limiter.ReserveSignupSendAsync("another@gmail.com", cooldown)).Should().BePositive();
    }

    [HumansFact]
    public async Task SignupSendReservations_NonGmailPlusAddressesRemainDistinct()
    {
        var cooldown = TimeSpan.FromSeconds(60);
        (await _limiter.ReserveSignupSendAsync("member@example.org", cooldown)).Should().Be(0);
        (await _limiter.ReserveSignupSendAsync("member+signup@example.org", cooldown)).Should().Be(0);
    }

    [HumansFact]
    public async Task RefusedSends_AreCountedUntilTheCooldownIsReleased()
    {
        var cooldown = TimeSpan.FromSeconds(60);
        await _limiter.ReserveSignupSendAsync("member@example.org", cooldown);

        (await _limiter.ReserveSignupSendAsync("member@example.org", cooldown)).Should().Be(1);
        (await _limiter.ReserveSignupSendAsync("member@example.org", cooldown)).Should().Be(2);

        _limiter.ReleaseSignupReservation("member@example.org");

        (await _limiter.ReserveSignupSendAsync("member@example.org", cooldown)).Should().Be(0);
        (await _limiter.ReserveSignupSendAsync("member@example.org", cooldown)).Should().Be(1);
    }

    public void Dispose() => _cache.Dispose();
}

using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Stripe.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Stripe.Tests.Services;

/// <summary>
/// The two boot jobs' one shared contract: neither may block, delay or fail startup, and
/// neither may act outside the environment it is gated to. Both fire-and-forget from
/// <c>StartAsync</c>, so these assert that <c>StartAsync</c> returns already-completed and then
/// wait for the background body to log its outcome.
/// </summary>
public class StripeBootJobTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a test that asserts <em>silence</em> waits before believing it. Asserting an
    /// absence here is time-bounded by nature — the registrar's fire-and-forget task is not
    /// observable from outside — so unlike <see cref="Settle"/>, which is an upper bound a
    /// passing test never reaches, this window is paid in full on every run. Do not shave it to
    /// tens of milliseconds: under CI load the <c>Task.Run</c> body may not have been scheduled
    /// yet, and the test would pass without the code having had a chance to speak.
    /// </summary>
    private static readonly TimeSpan SilenceWindow = TimeSpan.FromMilliseconds(500);

    [HumansFact]
    public async Task Smoke_probe_does_not_block_startup_and_warns_per_unconfigured_key()
    {
        var log = new CapturingLogger<StripeStartupSmokeService>();
        var probe = new StripeStartupSmokeService(Options.Create(new StripeSettings()), log);

        var start = probe.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue("boot must not wait on a Stripe round trip");
        // Tickets key, Store key, webhook secret — one warning each, and no throw.
        // Count, not Count(predicate): CapturingLogger appends to a plain List, so enumerating it
        // while the probe is still logging is a concurrent read/write. The count field is not an
        // enumeration; and with nothing configured the probe logs these three and then awaits an
        // empty probe set, so once the third lands nobody is writing and the assertion below is
        // free to enumerate. The dropped level predicate is not lost — the next line asserts it.
        await WaitUntil(() => log.Entries.Count >= 3);
        log.Entries.Should().HaveCount(3).And.OnlyContain(e => e.Level == LogLevel.Warning);

        await probe.StopAsync(CancellationToken.None);
    }

    [HumansFact]
    public async Task Registrar_stays_silent_without_its_own_key()
    {
        // Production and QA deliberately leave STRIPE_STORE_WEBHOOK_REGISTRAR_KEY unset. The
        // registrar must then do nothing at all — not even complain.
        var (registrar, log) = BuildRegistrar(new StripeSettings(), "https://5.n.burn.camp");

        var start = registrar.StartAsync(CancellationToken.None);
        start.IsCompleted.Should().BeTrue("boot must not wait on a Stripe round trip");

        // Not StartAndSettle: its wait is for an entry to appear, which is exactly what must not
        // happen here, so it could only ever time out. See SilenceWindow.
        await Task.Delay(SilenceWindow);
        await registrar.StopAsync(CancellationToken.None);

        log.Entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Registrar_refuses_a_host_it_does_not_own()
    {
        // A dev box or any unrecognized host: Stripe could not reach it, and registering would
        // burn an endpoint against the account quota.
        var (registrar, log) = BuildRegistrar(
            new StripeSettings { WebhookRegistrarKey = "rk_test_registrar" },
            "https://localhost:5001");

        var entries = await StartAndSettle(registrar, log);

        entries.Should().ContainSingle()
            .Which.Message.Should().Contain("not a recognized PR-preview host");
    }

    [HumansFact]
    public async Task Registrar_refuses_to_guess_a_url_when_base_url_is_unset()
    {
        var (registrar, log) = BuildRegistrar(
            new StripeSettings { WebhookRegistrarKey = "rk_test_registrar" },
            baseUrl: null);

        var entries = await StartAndSettle(registrar, log);

        entries.Should().ContainSingle()
            .Which.Message.Should().Contain("no Email:BaseUrl configured");
    }

    private static (StoreWebhookRegistrationService Registrar, CapturingLogger<StoreWebhookRegistrationService> Log)
        BuildRegistrar(StripeSettings settings, string? baseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Email:BaseUrl"] = baseUrl })
            .Build();
        var log = new CapturingLogger<StoreWebhookRegistrationService>();

        return (new StoreWebhookRegistrationService(
            Options.Create(settings), Options.Create(new GitHubSettings()), configuration, log), log);
    }

    private static async Task<IReadOnlyList<CapturingLogger<StoreWebhookRegistrationService>.LogEntry>>
        StartAndSettle(StoreWebhookRegistrationService registrar, CapturingLogger<StoreWebhookRegistrationService> log)
    {
        var start = registrar.StartAsync(CancellationToken.None);
        start.IsCompleted.Should().BeTrue("boot must not wait on a Stripe round trip");

        // Every caller of this helper expects exactly one entry, and every path they exercise
        // returns before any network call — so the wait is only for the background task to be
        // scheduled, and returns as soon as it has been. Settle is the upper bound, not the cost.
        await WaitUntil(() => log.Entries.Count > 0);
        await registrar.StopAsync(CancellationToken.None);
        return log.Entries;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}

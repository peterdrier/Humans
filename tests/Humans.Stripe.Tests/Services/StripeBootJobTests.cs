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

    [HumansFact]
    public async Task Smoke_probe_does_not_block_startup_and_warns_per_unconfigured_key()
    {
        var log = new CapturingLogger<StripeStartupSmokeService>();
        var probe = new StripeStartupSmokeService(Options.Create(new StripeSettings()), log);

        var start = probe.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue("boot must not wait on a Stripe round trip");
        // Tickets key, Store key, webhook secret — one warning each, and no throw.
        await WaitUntil(() => log.Entries.Count(e => e.Level == LogLevel.Warning) >= 3);
        log.Entries.Should().HaveCount(3).And.OnlyContain(e => e.Level == LogLevel.Warning);

        await probe.StopAsync(CancellationToken.None);
    }

    [HumansFact]
    public async Task Registrar_stays_silent_without_its_own_key()
    {
        // Production and QA deliberately leave STRIPE_STORE_WEBHOOK_REGISTRAR_KEY unset. The
        // registrar must then do nothing at all — not even complain.
        var (registrar, log) = BuildRegistrar(new StripeSettings(), "https://5.n.burn.camp");

        (await StartAndSettle(registrar, log)).Should().BeEmpty();
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

        // Every path under test returns before any network call, so it settles immediately; the
        // wait is only for the background task to be scheduled. A silent path has nothing to wait
        // for, so this drains the full window and asserts on what did not appear.
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

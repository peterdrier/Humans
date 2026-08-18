using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Holded;

/// <summary>
/// Holded's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// No GDPR contributor: the mirror has no user-scoped table. The member→creditor-account
/// binding stays in Finance, which owns that meaning and exports it.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Sentinel choice is load-bearing: "the sentinel exists" must PROVE this baseline already
        // ran. holded_ledger_lines cannot carry that proof — the historical root migration chain
        // and Finance's pre-split model both created it, so on existing databases it exists while
        // holded_accounts/holded_api_calls/holded_sync_states do not, and a sentinel hit would
        // record the baseline as applied with three tables missing. holded_accounts is created by
        // this baseline alone. Ordering still matters once per boot: sections migrate in name
        // order, so Finance's drop of its holded_ledger_lines/holded_sync_states runs before this
        // baseline recreates them under Holded ownership.
        services.AddSectionDbContext<HoldedDbContext>(sentinelTable: "holded_accounts");

        // The plan-tier call allowance, which no Holded endpoint reports (GET /usage's `limit`
        // is the billable-overage ceiling). Left at the default when unset or unparseable.
        services.Configure<HoldedSectionOptions>(opts =>
        {
            if (int.TryParse(configuration["Holded:MonthlyCallBudget"],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) && budget > 0)
                opts.MonthlyCallBudget = budget;
        });

        // The Holded API connector — typed HttpClient, its options, and the in-memory call log the
        // mirror drains into holded_api_calls. Registered here since G5 lane 4b-2f
        // (nobodies-collective/Humans#866); it was Humans.Web's AddHoldedConnector before that.
        services.Configure<HoldedClientOptions>(opts =>
        {
            // HOLDED_API_KEY_V2, not HOLDED_API_KEY: a v2-generated key is rejected by the v1 API
            // (probed 2026-08-11: 400 Invalid key), so a same-name cutover would break whichever
            // build held the wrong key. Distinct names let both keys coexist in the environment
            // while the old build drains; delete HOLDED_API_KEY once this build is confirmed live.
            opts.ApiKey = Environment.GetEnvironmentVariable("HOLDED_API_KEY_V2") ?? "";
            opts.BaseUrl = configuration["Holded:BaseUrl"] ?? "https://api.holded.com";
        });

        services.AddSingleton<Services.IHoldedCallLog, Services.HoldedCallLog>();

        services.AddHttpClient<IHoldedClient, Services.HoldedClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<HoldedClientOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IHoldedMirrorRepository, Repository>();
        services.AddScoped<Services.Service>();
        services.AddScoped<IHoldedService>(sp => sp.GetRequiredService<Services.Service>());
        services.AddScoped<Services.IHoldedAdminService>(sp => sp.GetRequiredService<Services.Service>());

        // The nightly pull's body (G5 step 6b). Its Hangfire target, HoldedSyncJob, is in
        // this project's Contracts/ folder since G5 lane 5b-5; only the registration is
        // Shell's, because the roll-call names each job by concrete type.
        services.AddScoped<IHoldedNightlySync, Services.HoldedNightlySync>();
    }
}

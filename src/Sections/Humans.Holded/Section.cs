using Humans.Finance.Contracts;
using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Holded.Contracts;
using Humans.Holded.Data;
using Humans.Holded.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Holded;

/// <summary>No GDPR contributor: no table is user-scoped. The member-to-creditor binding is Finance's, and Finance
/// exports it.</summary>
public sealed class Section : ISection, ISectionAdminNav, ISectionConfiguration, ISectionJobs
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // The sentinel must prove this baseline ran. holded_ledger_lines can't: the old root chain and Finance's
        // pre-split model both created it. holded_accounts is created by this baseline alone. Sections migrate in
        // name order, so Finance's drop of its old Holded tables runs before this baseline recreates them.
        services.AddSectionDbContext<HoldedDbContext>(sentinelTable: "holded_accounts");

        // The plan-tier allowance, which no endpoint reports. Left at the default when unset or unparseable.
        services.Configure<HoldedSectionOptions>(opts =>
        {
            if (int.TryParse(configuration["Holded:MonthlyCallBudget"],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) && budget > 0)
                opts.MonthlyCallBudget = budget;
        });

        services.Configure<HoldedClientOptions>(opts =>
        {
            // HOLDED_API_KEY_V2, not HOLDED_API_KEY: the v1 API rejects a v2 key, so the names must stay distinct.
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

        services.AddScoped<Services.IHoldedNightlySync, Services.HoldedNightlySync>();

        services.AddScoped<HoldedSyncJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Holded", [
            new("Holded", "Holded", "Index", null, null, "fa-solid fa-book", FinancePolicies.Read)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Holded:BaseUrl", "Holded");
        registry.RegisterEnvironmentVariable("HOLDED_API_KEY_V2", "Holded", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "holded-sync", typeof(HoldedSyncJob), "0 3 * * *");
    }
}

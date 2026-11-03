using Humans.Base.Configuration;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Pleo.Contracts;
using Humans.Pleo.Data;
using Humans.Pleo.Jobs;
using Humans.Pleo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Pleo;

/// <summary>No GDPR contributor: cardholders are mirrored by Pleo id and name only, bound to no member, and a sweep
/// drops anyone Pleo no longer returns.</summary>
public sealed class Section : ISection, ISectionAdminNav, ISectionConfiguration, ISectionJobs
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<PleoDbContext>(sentinelTable: "pleo_entries");

        services.Configure<PleoClientOptions>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("PLEO_API_KEY") ?? "";
            opts.CompanyId = Environment.GetEnvironmentVariable("PLEO_COMPANY_ID") ?? "";
            opts.BaseUrl = configuration["Pleo:BaseUrl"] ?? "https://external.pleo.io";
        });

        services.AddHttpClient<IPleoClient, PleoClient>((sp, client) =>
        {
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PleoClientOptions>>().Value.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<IPleoRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IPleoService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IPleoAdminService>(sp => sp.GetRequiredService<Service>());

        services.AddScoped<PleoSyncJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Pleo", [
            new("Pleo", "Pleo", "Index", null, null, "fa-solid fa-credit-card", FinancePolicies.Read)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Pleo:BaseUrl", "Pleo");
        registry.RegisterEnvironmentVariable("PLEO_API_KEY", "Pleo", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        registry.RegisterEnvironmentVariable("PLEO_COMPANY_ID", "Pleo", isSensitive: false,
            importance: ConfigurationImportance.Recommended);
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("pleo-sync", typeof(PleoSyncJob), "15 3 * * *");
    }
}

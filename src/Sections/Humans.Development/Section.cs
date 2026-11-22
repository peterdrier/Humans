using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Base.Demo;
using Humans.Base.Hosting;
using Humans.Development.Data;
using Humans.Development.Jobs;
using Humans.Development.Services;
using Humans.Development.Services.Demo;

namespace Humans.Development;

/// <summary>
/// Registers the dev seeders outside Production only, and fails closed: an environment name it cannot read registers
/// nothing. <c>Register</c> gets no <see cref="IHostEnvironment"/>, so the name comes from
/// <see cref="HostDefaults.EnvironmentKey"/> in configuration.
/// </summary>
public sealed class Section : ISection, ISectionAdminNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var environmentName = configuration[HostDefaults.EnvironmentKey];

        if (string.IsNullOrWhiteSpace(environmentName)
            || string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        services.AddScoped<DevelopmentCampRoleSeeder>();
        services.AddScoped<DevPersonaSeeder>();

        // Outside Production only, like everything above: the demo ledger's table never exists there.
        services.AddSectionDbContext<DevelopmentDbContext>(sentinelTable: "demo_ledger");
        services.AddSingleton<IDemoLedgerRepository, DemoLedgerRepository>();
        services.AddSingleton<DemoRunStatus>();
        services.AddScoped<DemoWorldRunner>();
        services.AddScoped<DemoWorldJob>();
        services.AddDemoStory<DemoCastStory>(configuration);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Development", [
            new("Seed data", "DevSeed", "Index", null, null, "fa-solid fa-seedling", PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction())
        ])
    ];
}

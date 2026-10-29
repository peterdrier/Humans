using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Development.Services;

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
        services.AddScoped<DevelopmentDashboardSeeder>();
        services.AddScoped<DevPersonaSeeder>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Development", [
            new("Seed data", "DevSeed", "Index", null, null, "fa-solid fa-seedling", PolicyNames.AdminOnly,
                 EnvironmentGate: env => env.IsDevelopment())
        ])
    ];
}

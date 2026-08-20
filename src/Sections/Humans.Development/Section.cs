using Humans.Base.Interfaces;
using Humans.Development.Services;

namespace Humans.Development;

/// <summary>
/// Development's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// The three dev fixture seeders are registered <em>outside Production only</em>, which is
/// where <c>Program.cs</c> registered them before the move. That is the one thing this
/// section's registration has to get right: <c>DevLoginController</c> takes
/// <see cref="DevPersonaSeeder"/> as a constructor dependency, so leaving the seeder out of
/// the Production graph is what makes Shell's <c>DevLoginControllerExclusionProvider</c>
/// necessary rather than decorative.
/// </para>
/// <para>
/// <see cref="ISection.Register"/> takes no <see cref="IHostEnvironment"/> (Email's finding
/// at its own G5), and unlike Email's Production-SMTP guard there is no half of this decision
/// that can stay in Shell: the seeders are <c>internal</c> to this assembly, so Shell cannot
/// name them to register them conditionally. The environment is reachable from the
/// configuration Shell passes in — <see cref="HostDefaults.EnvironmentKey"/> is host
/// configuration and <c>WebApplicationBuilder.Configuration</c> carries it — and the check is
/// deliberately written to fail <em>closed</em>: an environment name this section cannot read
/// registers nothing, so the failure mode is dev login 404ing in the test host (loud, and the
/// integration suite drives <c>/dev/login/{slug}</c> on every sign-in) rather than three dev
/// seeders quietly reaching Production.
/// </para>
/// <para>
/// Nothing else is registered here. The section owns no tables — no <c>AddSectionDbContext</c>
/// call, no repository — and both controllers' authorization policies stay in Shell's
/// <c>AuthorizationPolicyExtensions</c> (design §8).
/// </para>
/// </remarks>
public sealed class Section : ISection
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
}

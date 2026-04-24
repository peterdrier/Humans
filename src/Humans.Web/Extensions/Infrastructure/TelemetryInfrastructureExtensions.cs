using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Services.Auth;
using Humans.Application.Services.Consent;
using Humans.Application.Services.Email;
using Humans.Application.Services.Governance;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Services.Legal;
using Humans.Application.Services.Onboarding;
using Humans.Application.Services.Profile;
using Humans.Application.Services.Teams;
using Humans.Application.Services.Users;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TelemetryInfrastructureExtensions
{
    internal static IServiceCollection AddTelemetryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));

        // Push-model metrics registration (issue nobodies-collective/Humans#580).
        // Each section's contributor is a Singleton so counter references and
        // gauge backing state persist across scopes. The contributor is
        // forwarded through both its section-specific metrics interface (so
        // call sites inject a narrow surface) and the shared
        // IMetricsContributor interface (so HumansMetricsService can iterate
        // and Initialize them at startup).

        // Email section — has an IEmailMetrics counter/gauge surface.
        services.AddSingleton<EmailMetricsContributor>();
        services.AddSingleton<IEmailMetrics>(sp => sp.GetRequiredService<EmailMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<EmailMetricsContributor>());

        // Consent section — counter + two gauges.
        services.AddSingleton<ConsentMetricsContributor>();
        services.AddSingleton<IConsentMetrics>(sp => sp.GetRequiredService<ConsentMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<ConsentMetricsContributor>());

        // Onboarding section — counter-only surface.
        services.AddSingleton<OnboardingMetricsContributor>();
        services.AddSingleton<IOnboardingMetrics>(sp => sp.GetRequiredService<OnboardingMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<OnboardingMetricsContributor>());

        // Governance section — IApplicationMetrics counter + two gauges.
        services.AddSingleton<GovernanceMetricsContributor>();
        services.AddSingleton<IApplicationMetrics>(sp => sp.GetRequiredService<GovernanceMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<GovernanceMetricsContributor>());

        // Google Integration — IGoogleSyncMetrics counter + two gauges.
        services.AddSingleton<GoogleIntegrationMetricsContributor>();
        services.AddSingleton<IGoogleSyncMetrics>(sp => sp.GetRequiredService<GoogleIntegrationMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<GoogleIntegrationMetricsContributor>());

        // Cross-cutting job-run counter (all Hangfire jobs).
        services.AddSingleton<JobRunMetricsContributor>();
        services.AddSingleton<IJobRunMetrics>(sp => sp.GetRequiredService<JobRunMetricsContributor>());
        services.AddSingleton<IMetricsContributor>(sp => sp.GetRequiredService<JobRunMetricsContributor>());

        // Gauge-only contributors (no public section interface).
        services.AddSingleton<IMetricsContributor, TeamsMetricsContributor>();
        services.AddSingleton<IMetricsContributor, ProfilesMetricsContributor>();
        services.AddSingleton<IMetricsContributor, UsersMetricsContributor>();
        services.AddSingleton<IMetricsContributor, AuthMetricsContributor>();
        services.AddSingleton<IMetricsContributor, LegalMetricsContributor>();

        // Registry itself — Singleton so the Meter/Timer live for the app lifetime.
        // Resolves IEnumerable<IMetricsContributor> from DI and invokes Initialize
        // on each during construction.
        services.AddSingleton<IHumansMetrics, HumansMetricsService>();

        return services;
    }
}

using Humans.Application.Interfaces;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Onboarding;

/// <summary>
/// Onboarding's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// No <c>AddSectionDbContext</c> line: the section owns no tables. It is a pure
/// orchestrator over Profiles, Consent, Teams and Governance and routes every read and
/// write through the owning section's service interface, so there is no repository and no
/// <c>PeeledConfigurationNamespaces</c> row either.
/// <para>
/// One instance serves three registrations — the section's own full interface, the leaf's
/// two-member intake contract, and the concrete type the controllers take — so the
/// director's state and logging stay single-instance per request.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // No back-call from leaf services into this director: ProfileService and
        // ConsentService do not inject IOnboardingIntake. The consent-check threshold
        // (SetConsentCheckPendingIfEligibleAsync) is invoked by controllers as a peer
        // call after the leaf-service write.
        services.AddScoped<OnboardingService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingService>());
        services.AddScoped<IOnboardingIntake>(sp => sp.GetRequiredService<OnboardingService>());

        services.AddScoped<IOnboardingWidgetState, OnboardingWidgetState>();
        services.AddScoped<IOnboardingWidgetSessionState, HttpOnboardingWidgetSessionState>();
    }
}

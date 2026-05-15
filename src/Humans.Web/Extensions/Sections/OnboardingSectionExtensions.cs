using Humans.Application.Interfaces.Onboarding;
using Humans.Web.Services.Onboarding;
using OnboardingOrchestratorService = Humans.Application.Services.Onboarding.OnboardingService;
using OnboardingWidgetStateService = Humans.Application.Services.Onboarding.OnboardingWidgetState;

namespace Humans.Web.Extensions.Sections;

internal static class OnboardingSectionExtensions
{
    internal static IServiceCollection AddOnboardingSection(this IServiceCollection services)
    {
        // Onboarding — orchestrator only (owns no tables). Lives in Humans.Application
        // per design-rules §2b; routes all reads/writes through owning-section
        // service interfaces (IProfileService, IUserService, IApplicationDecisionService,
        // ISystemTeamSync, etc.). Takes no DbContext dependency.
        //
        // No back-call from leaf services into this director: ProfileService and
        // ConsentService own their own threshold checks via
        // IProfileService.TrySetConsentCheckPendingIfEligibleAsync.
        services.AddScoped<OnboardingOrchestratorService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());

        services.AddScoped<IOnboardingWidgetState, OnboardingWidgetStateService>();
        services.AddScoped<IOnboardingWidgetSessionState, HttpOnboardingWidgetSessionState>();

        return services;
    }
}

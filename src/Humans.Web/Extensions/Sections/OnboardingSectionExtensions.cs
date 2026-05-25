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
        // service interfaces (IUserService, IApplicationDecisionService,
        // ISystemTeamSync, IAuditLogService, etc.). Takes no DbContext dependency.
        //
        // No back-call from leaf services into this director: ProfileService and
        // ConsentService do not inject IOnboardingService. The consent-check
        // threshold (IOnboardingService.SetConsentCheckPendingIfEligibleAsync) is
        // invoked by controllers as a peer call after the leaf-service write.
        services.AddScoped<OnboardingOrchestratorService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingOrchestratorService>());

        services.AddScoped<IOnboardingWidgetState, OnboardingWidgetStateService>();
        services.AddScoped<IOnboardingWidgetSessionState, HttpOnboardingWidgetSessionState>();

        return services;
    }
}

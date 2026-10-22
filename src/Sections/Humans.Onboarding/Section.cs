using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Onboarding;

public sealed class Section : ISection, IIssueQueueOwner
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<OnboardingService>();
        services.AddScoped<IOnboardingService>(sp => sp.GetRequiredService<OnboardingService>());
        services.AddScoped<IOnboardingIntake>(sp => sp.GetRequiredService<OnboardingService>());

        services.AddScoped<OnboardingEmails>();
        services.AddScoped<IEmailPreviewContributor, OnboardingEmailPreviews>();

        services.AddScoped<IOnboardingWidgetState, OnboardingWidgetState>();
        services.AddScoped<IOnboardingWidgetSessionState, HttpOnboardingWidgetSessionState>();
    }

    string IIssueQueueOwner.QueueKey => "Onboarding";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Onboarding";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["onboardingreview"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.HumanAdmin];
}

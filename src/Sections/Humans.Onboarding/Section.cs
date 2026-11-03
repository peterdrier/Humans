using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Consent.Contracts;
using Humans.Email.Contracts;
using Humans.Issues.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Humans.Onboarding.ViewComponents;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Onboarding;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionChrome
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, OnboardingAuditActions>();
        services.AddSingleton<INotificationSourceContributor, OnboardingNotificationSources>();
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

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => [];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [ConsentRoles.ConsentCoordinator, ShiftsRoles.ShiftsAdmin, UsersRoles.UsersAdmin];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.AboveContent, typeof(OnboardingProgressBannerViewComponent))];
}

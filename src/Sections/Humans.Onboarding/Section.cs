using Humans.Base.Authorization;
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
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Onboarding;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionChrome,
    ISectionHelp
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
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

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["onboardingreview"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [ConsentRoles.ConsentCoordinator, ShiftsRoles.ShiftsAdmin, UsersRoles.UsersAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "OnboardingReview",
            SectionName = "Onboarding Review",
            Order = 50,
            Roles = ["ConsentCoordinator", "ShiftsAdmin", "Board"],
            Features =
            [
                Of("View onboarding queue", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Allowed), ("Board", Allowed)),
                Of("Clear consent checks", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Denied), ("Board", Allowed)),
                Of("Flag / reject signup", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Denied), ("Board", Allowed)),
                Of("Board voting", ("ConsentCoordinator", Denied), ("ShiftsAdmin", Denied), ("Board", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Onboarding", [
            new("Review", "OnboardingReview", "Index", null, null, "fa-solid fa-clipboard-check", PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueueAsync)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.AboveContent, typeof(OnboardingProgressBannerViewComponent))];

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "OnboardingReview", 70),
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueueAsync(IServiceProvider sp)
    {
        var users = sp.GetRequiredService<IUserService>();
        var reviews = await sp.GetRequiredService<IConsentService>().GetConsentReviewsAsync();
        var count = (await users.GetAllUserInfosAsync()).Count(u =>
            u.NeedsConsentReview(reviews.GetValueOrDefault(u.Id) ?? ConsentReviewInfo.None(u.Id)));
        return count > 0 ? count : null;
    }
}

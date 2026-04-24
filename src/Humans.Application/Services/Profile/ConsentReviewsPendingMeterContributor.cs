using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Meter: number of profiles awaiting a Consent Coordinator review. Registered by
/// the Profile section (which owns the <c>profiles</c> table) per the push-model
/// design in issue nobodies-collective/Humans#581.
/// </summary>
public sealed class ConsentReviewsPendingMeterContributor : INotificationMeterContributor
{
    private readonly IProfileService _profileService;

    public ConsentReviewsPendingMeterContributor(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public string Key => "ConsentReviewsPending";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) =>
        user.IsInRole(RoleNames.ConsentCoordinator);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var count = await _profileService.GetConsentReviewPendingCountAsync(cancellationToken);
        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Consent reviews pending",
            Count = count,
            ActionUrl = "/OnboardingReview",
            Priority = 10,
        };
    }
}

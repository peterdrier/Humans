using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Meter: number of profiles in the onboarding queue excluding those in consent
/// review. Matches the board-digest "still onboarding" semantics — count is
/// <c>totalNotApproved - consentReviewsPending</c>, clamped at zero. Registered by
/// the Profile section per the push-model design in issue nobodies-collective/Humans#581.
/// </summary>
public sealed class OnboardingPendingMeterContributor : INotificationMeterContributor
{
    private readonly IProfileService _profileService;

    public OnboardingPendingMeterContributor(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public string Key => "OnboardingPending";

    public NotificationMeterScope Scope => NotificationMeterScope.Global;

    public bool IsVisibleTo(ClaimsPrincipal user) =>
        user.IsInRole(RoleNames.Board) || user.IsInRole(RoleNames.VolunteerCoordinator);

    public async Task<NotificationMeter?> BuildMeterAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var totalNotApproved =
            await _profileService.GetNotApprovedAndNotSuspendedCountAsync(cancellationToken);
        var consentReviewsPending =
            await _profileService.GetConsentReviewPendingCountAsync(cancellationToken);

        var count = totalNotApproved - consentReviewsPending;
        if (count <= 0) return null;

        return new NotificationMeter
        {
            Title = "Onboarding profiles pending",
            Count = count,
            ActionUrl = "/OnboardingReview",
            Priority = 6,
        };
    }
}

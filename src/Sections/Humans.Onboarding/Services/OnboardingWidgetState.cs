using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Constants;
using Humans.Users.Contracts;

namespace Humans.Onboarding.Services;

internal sealed class OnboardingWidgetState(
    IUserService users,
    IShiftView shiftView,
    IMembershipCalculator membership,
    ISettingsService settingsService,
    IConsentService consents,
    IOnboardingWidgetSessionState session) : IOnboardingWidgetState
{
    public async Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default)
    {
        // HasRequiredNameFields, not IsStub: catches Active profiles with blank names.
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null || !info.HasRequiredNameFields)
            return OnboardingWidgetStep.Names;
        userId = info.Id;

        if (await membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct))
            return OnboardingWidgetStep.Complete;

        // Returning member with any prior signature → skip Shifts, go to Consents (renew/new docs).
        var signedVersions = await consents.GetConsentedVersionIdsAsync(userId, ct);
        if (signedVersions.Count > 0)
            return OnboardingWidgetStep.Consents;

        var hasSkip = session.ShiftSkipActive;

        var activeEvent = await settingsService.GetActiveEventSettingsAsync(ct);
        var hasCurrentEventSignup = false;
        if (activeEvent is not null)
        {
            var shifts = await shiftView.GetUserAsync(userId, ct);
            hasCurrentEventSignup = shifts.HasShift;
        }

        return (hasSkip || hasCurrentEventSignup)
            ? OnboardingWidgetStep.Consents
            : OnboardingWidgetStep.Shifts;
    }
}

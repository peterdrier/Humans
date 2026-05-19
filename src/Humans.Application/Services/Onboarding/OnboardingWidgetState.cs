using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Onboarding;

public class OnboardingWidgetState(
    IUserService users,
    IShiftSignupService signups,
    IMembershipCalculator membership,
    IShiftManagementService shiftMgmt,
    IConsentService consents,
    IOnboardingWidgetSessionState session) : IOnboardingWidgetState
{
    public async Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default)
    {
        if (await membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct))
            return OnboardingWidgetStep.Complete;

        // HasRequiredNameFields (not IsStub) catches Active profiles with blank names from data drift.
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null || !info.HasRequiredNameFields)
            return OnboardingWidgetStep.Names;

        // Returning member with any prior signature → skip Shifts, go to Consents (renew/new docs).
        var requiredRows = await consents.GetRequiredConsentRowsForUserAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (requiredRows.Any(r => r.Signed))
            return OnboardingWidgetStep.Consents;

        var hasSkip = session.ShiftSkipActive;

        var activeEvent = await shiftMgmt.GetActiveAsync();
        var hasCurrentEventSignup = false;
        if (activeEvent is not null)
        {
            var (shiftIds, _) = await signups.GetActiveSignupStatusesAsync(userId, activeEvent.Id);
            hasCurrentEventSignup = shiftIds.Count > 0;
        }

        return (hasSkip || hasCurrentEventSignup)
            ? OnboardingWidgetStep.Consents
            : OnboardingWidgetStep.Shifts;
    }
}

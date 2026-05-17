using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;

namespace Humans.Application.Services.Onboarding;

public class OnboardingWidgetState : IOnboardingWidgetState
{
    private readonly IUserService _users;
    private readonly IShiftSignupService _signups;
    private readonly IMembershipCalculator _membership;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IConsentService _consents;
    private readonly IOnboardingWidgetSessionState _session;

    public OnboardingWidgetState(
        IUserService users,
        IShiftSignupService signups,
        IMembershipCalculator membership,
        IShiftManagementService shiftMgmt,
        IConsentService consents,
        IOnboardingWidgetSessionState session)
    {
        _users = users;
        _signups = signups;
        _membership = membership;
        _shiftMgmt = shiftMgmt;
        _consents = consents;
        _session = session;
    }

    public async Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default)
    {
        if (await _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct))
            return OnboardingWidgetStep.Complete;

        // HasRequiredNameFields (not IsStub) catches Active profiles with blank names from data drift.
        var info = await _users.GetUserInfoAsync(userId, ct);
        if (info is null || !info.HasRequiredNameFields)
            return OnboardingWidgetStep.Names;

        // Returning member with any prior signature → skip Shifts, go to Consents (renew/new docs).
        var requiredRows = await _consents.GetRequiredConsentRowsForUserAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (requiredRows.Any(r => r.Signed))
            return OnboardingWidgetStep.Consents;

        var hasSkip = _session.ShiftSkipActive;

        var activeEvent = await _shiftMgmt.GetActiveAsync();
        var hasCurrentEventSignup = false;
        if (activeEvent is not null)
        {
            var (shiftIds, _) = await _signups.GetActiveSignupStatusesAsync(userId, activeEvent.Id);
            hasCurrentEventSignup = shiftIds.Count > 0;
        }

        return (hasSkip || hasCurrentEventSignup)
            ? OnboardingWidgetStep.Consents
            : OnboardingWidgetStep.Shifts;
    }
}

using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Http;

namespace Humans.Application.Services.Onboarding;

public class OnboardingWidgetState : IOnboardingWidgetState
{
    /// <summary>Session key set by `/OnboardingWidget/Skip` and read here.</summary>
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    private readonly IProfileService _profile;
    private readonly IShiftSignupService _signups;
    private readonly IMembershipCalculator _membership;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IConsentService _consents;
    private readonly IHttpContextAccessor _http;

    public OnboardingWidgetState(
        IProfileService profile,
        IShiftSignupService signups,
        IMembershipCalculator membership,
        IShiftManagementService shiftMgmt,
        IConsentService consents,
        IHttpContextAccessor http)
    {
        _profile = profile;
        _signups = signups;
        _membership = membership;
        _shiftMgmt = shiftMgmt;
        _consents = consents;
        _http = http;
    }

    public async Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default)
    {
        // Consents-complete short-circuits everyone past the widget.
        if (await _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct))
            return OnboardingWidgetStep.Complete;

        var profile = await _profile.GetProfileAsync(userId, ct);
        if (profile is null)
            return OnboardingWidgetStep.Names;

        // Returning member: any signed row in the current required Volunteer
        // set means they're a known member with consents to renew (annual
        // expiration / newly-added required doc) — skip the Shifts step and
        // send them straight to Consents.
        var requiredRows = await _consents.GetRequiredConsentRowsForUserAsync(
            userId, SystemTeamIds.Volunteers, ct);
        if (requiredRows.Any(r => r.Signed))
            return OnboardingWidgetStep.Consents;

        var hasSkip = string.Equals(
            _http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
            "true",
            StringComparison.Ordinal);

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

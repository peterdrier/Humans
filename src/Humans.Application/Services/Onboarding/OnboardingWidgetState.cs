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
    private readonly IHttpContextAccessor _http;

    public OnboardingWidgetState(
        IProfileService profile,
        IShiftSignupService signups,
        IMembershipCalculator membership,
        IShiftManagementService shiftMgmt,
        IHttpContextAccessor http)
    {
        _profile = profile;
        _signups = signups;
        _membership = membership;
        _shiftMgmt = shiftMgmt;
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

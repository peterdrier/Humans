using System.Security.Claims;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a plain Volunteer.</summary>
public sealed class MemberTermStatusViewComponent(
    IMembershipCalculator membershipCalculator,
    IGovernanceService applicationService,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var ct = HttpContext.RequestAborted;
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Content(string.Empty);
        }

        var tier = await applicationService.GetMembershipTierAsync(userId, ct);
        var isVolunteerMember = (await membershipCalculator.GetMembershipSnapshotAsync(userId, ct)).IsVolunteerMember;

        if (!isVolunteerMember || tier == MembershipTier.Volunteer)
        {
            return Content(string.Empty);
        }

        var applications = await applicationService.GetUserApplicationsAsync(userId, ct);
        var (expiresAt, expiresSoon, expired) = ComputeTermState(applications, tier);
        var hasPendingApplication = applications.MaxBy(a => a.SubmittedAt)?.Status == ApplicationStatus.Submitted;

        return View(new MemberTermStatusViewModel(
            tier,
            expiresAt?.AtMidnight().InUtc().ToDateTimeUtc(),
            expiresSoon,
            expired,
            hasPendingApplication));
    }

    private (LocalDate? ExpiresAt, bool ExpiresSoon, bool Expired) ComputeTermState(
        IReadOnlyList<UserApplicationSnapshot> applications,
        MembershipTier currentTier)
    {
        var latestApprovedApp = applications
            .Where(a => a.Status == ApplicationStatus.Approved
                && a.MembershipTier == currentTier
                && a.TermExpiresAt is not null)
            .OrderByDescending(a => a.TermExpiresAt)
            .FirstOrDefault();

        if (latestApprovedApp?.TermExpiresAt is null)
        {
            return (null, false, false);
        }

        var today = clock.GetCurrentInstant().InUtc().Date;
        var expiryDate = latestApprovedApp.TermExpiresAt.Value;
        var expired = expiryDate < today;
        var expiresSoon = !expired && expiryDate <= today.PlusDays(90);

        return (expiryDate, expiresSoon, expired);
    }
}

internal sealed record MemberTermStatusViewModel(
    MembershipTier MembershipTier,
    DateTime? TermExpiresAt,
    bool TermExpiresSoon,
    bool TermExpired,
    bool HasPendingApplication);

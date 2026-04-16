using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;

namespace Humans.Web.ViewComponents;

public class ThingsToDoViewComponent : ViewComponent
{
    private readonly IProfileService _profileService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<ThingsToDoViewComponent> _logger;

    public ThingsToDoViewComponent(
        IProfileService profileService,
        IShiftManagementService shiftMgmt,
        IMembershipCalculator membershipCalculator,
        IStringLocalizer<SharedResource> localizer,
        ILogger<ThingsToDoViewComponent> logger)
    {
        _profileService = profileService;
        _shiftMgmt = shiftMgmt;
        _membershipCalculator = membershipCalculator;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        bool isVolunteerMember,
        bool hasShiftSignups)
    {
        var model = new ThingsToDoViewModel();

        try
        {
            var profile = await _profileService.GetProfileAsync(userId);
            var membershipSnapshot = await _membershipCalculator.GetMembershipSnapshotAsync(userId);

            var profileComplete = profile is not null && !string.IsNullOrEmpty(profile.FirstName);
            var consentsComplete = membershipSnapshot.PendingConsentCount == 0
                                   && membershipSnapshot.RequiredConsentCount > 0;

            // 1. Complete your profile
            model.Items.Add(new TodoItem
            {
                Key = "profile",
                Title = _localizer["Todo_Profile_Title"].Value,
                Description = profileComplete
                    ? _localizer["Todo_Profile_Done"].Value
                    : _localizer["Todo_Profile_Pending"].Value,
                IsDone = profileComplete,
                ActionUrl = profileComplete ? null : Url.Action("Edit", "Profile"),
                ActionText = profileComplete ? null : _localizer["Todo_Profile_Action"].Value,
                IconClass = "fa-solid fa-user"
            });

            // 2. Accept agreements
            if (membershipSnapshot.RequiredConsentCount > 0)
            {
                model.Items.Add(new TodoItem
                {
                    Key = "consents",
                    Title = _localizer["Todo_Consents_Title"].Value,
                    Description = consentsComplete
                        ? _localizer["Todo_Consents_Done"].Value
                        : string.Format(CultureInfo.CurrentCulture, _localizer["Todo_Consents_Pending"].Value,
                            membershipSnapshot.PendingConsentCount, membershipSnapshot.RequiredConsentCount),
                    IsDone = consentsComplete,
                    ActionUrl = consentsComplete ? null : Url.Action("Index", "Consent"),
                    ActionText = consentsComplete ? null : _localizer["Todo_Consents_Action"].Value,
                    IconClass = "fa-solid fa-file-signature"
                });
            }

            // 3. Consent check clearance (non-volunteers only)
            if (!isVolunteerMember)
            {
                var consentCheckStatus = profile?.ConsentCheckStatus;
                var consentCheckCleared = consentCheckStatus == ConsentCheckStatus.Cleared;

                model.Items.Add(new TodoItem
                {
                    Key = "consent-check",
                    Title = _localizer["Todo_ConsentCheck_Title"].Value,
                    Description = consentCheckCleared
                        ? _localizer["Todo_ConsentCheck_Done"].Value
                        : _localizer["Todo_ConsentCheck_Pending"].Value,
                    IsDone = consentCheckCleared,
                    ActionUrl = null,
                    ActionText = null,
                    IconClass = "fa-solid fa-clipboard-check"
                });
            }

            // 4. Set shift preferences (only when user has shift signups)
            if (hasShiftSignups)
            {
                var needsShiftInfo = false;
                try
                {
                    var shiftProfile = await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
                    needsShiftInfo = shiftProfile is null || IsShiftProfileEmpty(shiftProfile);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check shift profile for ThingsToDo component, user {UserId}", userId);
                }

                model.Items.Add(new TodoItem
                {
                    Key = "shift-info",
                    Title = _localizer["Todo_ShiftInfo_Title"].Value,
                    Description = needsShiftInfo
                        ? _localizer["Todo_ShiftInfo_Pending"].Value
                        : _localizer["Todo_ShiftInfo_Done"].Value,
                    IsDone = !needsShiftInfo,
                    ActionUrl = needsShiftInfo ? Url.Action("ShiftInfo", "Profile") : null,
                    ActionText = needsShiftInfo ? _localizer["Todo_ShiftInfo_Action"].Value : null,
                    IconClass = "fa-solid fa-calendar-check"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ThingsToDo data for user {UserId}", userId);
            return Content(string.Empty);
        }

        // Hide entirely when all items are done
        if (!model.HasAnyItems || model.AllDone)
        {
            return Content(string.Empty);
        }

        return View(model);
    }

    private static bool IsShiftProfileEmpty(VolunteerEventProfile profile)
    {
        return profile.Skills.Count == 0
            && profile.Quirks.Count == 0
            && profile.Languages.Count == 0
            && profile.Allergies.Count == 0
            && profile.Intolerances.Count == 0
            && string.IsNullOrEmpty(profile.DietaryPreference)
            && string.IsNullOrEmpty(profile.MedicalConditions);
    }
}

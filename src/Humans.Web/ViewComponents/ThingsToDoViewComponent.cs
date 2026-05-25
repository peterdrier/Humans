using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.ViewComponents;

public class ThingsToDoViewComponent(
    IUserServiceRead userService,
    IShiftManagementService shiftMgmt,
    IMembershipCalculator membershipCalculator,
    IStringLocalizer<SharedResource> localizer,
    ILogger<ThingsToDoViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        bool isVolunteerMember,
        bool hasShiftSignups,
        int profileCompletionPercent)
    {
        var model = new ThingsToDoViewModel();

        try
        {
            var profile = (await userService.GetUserInfoAsync(userId))?.Profile;
            var membershipSnapshot = await membershipCalculator.GetMembershipSnapshotAsync(userId);

            // Hidden/derived required fields can cap real-user completion in the
            // 90–95% range. Treat 80% as "complete enough" so the nudge stops
            // shouting at people who can't push it higher.
            var profileComplete = profileCompletionPercent >= 80;
            var consentsComplete = membershipSnapshot.PendingConsentCount == 0
                                   && membershipSnapshot.RequiredConsentCount > 0;

            // 1. Complete your profile
            model.Items.Add(new TodoItem
            {
                Key = "profile",
                Title = localizer["Todo_Profile_Title"].Value,
                Description = profileComplete
                    ? localizer["Todo_Profile_Done"].Value
                    : string.Format(CultureInfo.CurrentCulture,
                        localizer["Dashboard_ProfileCompletionPercent"].Value,
                        profileCompletionPercent),
                IsDone = profileComplete,
                ActionUrl = profileComplete ? null : Url.Action("Edit", "Profile"),
                ActionText = profileComplete ? null : localizer["Todo_Profile_Action"].Value,
                IconClass = "fa-solid fa-user",
                PercentComplete = profileComplete ? null : profileCompletionPercent,
            });

            // 2. Accept agreements
            if (membershipSnapshot.RequiredConsentCount > 0)
            {
                model.Items.Add(new TodoItem
                {
                    Key = "consents",
                    Title = localizer["Todo_Consents_Title"].Value,
                    Description = consentsComplete
                        ? localizer["Todo_Consents_Done"].Value
                        : string.Format(CultureInfo.CurrentCulture, localizer["Todo_Consents_Pending"].Value,
                            membershipSnapshot.PendingConsentCount, membershipSnapshot.RequiredConsentCount),
                    IsDone = consentsComplete,
                    ActionUrl = consentsComplete ? null : Url.Action("Index", "Consent"),
                    ActionText = consentsComplete ? null : localizer["Todo_Consents_Action"].Value,
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
                    Title = localizer["Todo_ConsentCheck_Title"].Value,
                    Description = consentCheckCleared
                        ? localizer["Todo_ConsentCheck_Done"].Value
                        : localizer["Todo_ConsentCheck_Pending"].Value,
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
                    var shiftProfile = await shiftMgmt.GetShiftProfileAsync(userId);
                    needsShiftInfo = shiftProfile is null || IsShiftProfileEmpty(shiftProfile);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to check shift profile for ThingsToDo component, user {UserId}", userId);
                }

                model.Items.Add(new TodoItem
                {
                    Key = "shift-info",
                    Title = localizer["Todo_ShiftInfo_Title"].Value,
                    Description = needsShiftInfo
                        ? localizer["Todo_ShiftInfo_Pending"].Value
                        : localizer["Todo_ShiftInfo_Done"].Value,
                    IsDone = !needsShiftInfo,
                    ActionUrl = needsShiftInfo ? Url.Action("ShiftInfo", "Profile") : null,
                    ActionText = needsShiftInfo ? localizer["Todo_ShiftInfo_Action"].Value : null,
                    IconClass = "fa-solid fa-calendar-check"
                });
            }

            // 5. Dietary & medical nudge — fires whenever DietaryPreference is empty.
            // Copy varies by whether the user has an active qualifying signup; the
            // item is the same Key either way so it disappears with the rest of the
            // card when DietaryPreference becomes non-empty.
            // See docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
            try
            {
                // Dietary now lives on Profile (already loaded as `profile` above).
                var dietaryEmpty = string.IsNullOrEmpty(profile?.DietaryPreference);
                if (dietaryEmpty)
                {
                    var hasQualifyingSignup = await shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
                    var descriptionKey = hasQualifyingSignup
                        ? "Todo_DietaryMedical_Pending"
                        : "Todo_DietaryMedical_NoShift_Pending";
                    model.Items.Add(new TodoItem
                    {
                        Key = "dietary-medical",
                        Title = localizer["Todo_DietaryMedical_Title"].Value,
                        Description = localizer[descriptionKey].Value,
                        IsDone = false,
                        ActionUrl = Url.Action("DietaryMedical", "Profile"),
                        ActionText = localizer["Todo_DietaryMedical_Action"].Value,
                        IconClass = "fa-solid fa-utensils",
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to check dietary/medical nudge for user {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load ThingsToDo data for user {UserId}", userId);
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
            && profile.Languages.Count == 0;
    }
}

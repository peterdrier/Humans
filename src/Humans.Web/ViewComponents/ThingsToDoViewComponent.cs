using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;

namespace Humans.Web.ViewComponents;

public class ThingsToDoViewComponent : ViewComponent
{
    private readonly IProfileService _profileService;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly ILogger<ThingsToDoViewComponent> _logger;

    public ThingsToDoViewComponent(
        IProfileService profileService,
        IMembershipCalculator membershipCalculator,
        ILogger<ThingsToDoViewComponent> logger)
    {
        _profileService = profileService;
        _membershipCalculator = membershipCalculator;
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
                Title = "Complete your profile",
                Description = profileComplete
                    ? "Your profile is complete."
                    : "Add your information so the community can get to know you.",
                IsDone = profileComplete,
                ActionUrl = profileComplete ? null : Url.Action("Edit", "Profile"),
                ActionText = profileComplete ? null : "Edit Profile",
                IconClass = "fa-solid fa-user"
            });

            // 2. Accept agreements
            if (membershipSnapshot.RequiredConsentCount > 0)
            {
                model.Items.Add(new TodoItem
                {
                    Key = "consents",
                    Title = "Accept agreements",
                    Description = consentsComplete
                        ? "All agreements are up to date."
                        : $"{membershipSnapshot.PendingConsentCount} of {membershipSnapshot.RequiredConsentCount} documents need your consent.",
                    IsDone = consentsComplete,
                    ActionUrl = consentsComplete ? null : Url.Action("Index", "Consent"),
                    ActionText = consentsComplete ? null : "Review Agreements",
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
                    Title = "Coordinator review",
                    Description = consentCheckCleared
                        ? "A coordinator has reviewed and cleared your documents."
                        : "A coordinator will review your documents once profile and agreements are complete.",
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
                    var shiftProfile = await _profileService.GetShiftProfileAsync(userId, includeMedical: false);
                    needsShiftInfo = shiftProfile is null || IsShiftProfileEmpty(shiftProfile);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check shift profile for ThingsToDo component, user {UserId}", userId);
                }

                model.Items.Add(new TodoItem
                {
                    Key = "shift-info",
                    Title = "Set your shift preferences",
                    Description = needsShiftInfo
                        ? "Fill out your shift info so coordinators know your skills and preferences."
                        : "Your shift preferences are set.",
                    IsDone = !needsShiftInfo,
                    ActionUrl = needsShiftInfo ? Url.Action("ShiftInfo", "Profile") : null,
                    ActionText = needsShiftInfo ? "Fill Out Shift Info" : null,
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

using Humans.Consent.Contracts;
using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Cantina.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Humans.Users;

/// <summary>
/// Users' entries on the member dashboard's things-to-do list: profile completion, the
/// consent-check clearance non-volunteers wait on, and the optional medical-conditions prompt.
/// Cantina contributes the dietary nudge the last one pairs with (its Docs/features/dietary-nudge.md).
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return [];
        }

        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var logger = services.GetRequiredService<ILogger<SectionThingsToDo>>();
        var profile = (await services.GetRequiredService<IUserService>().GetUserInfoAsync(userId))?.Profile;
        var isVolunteerMember = (await services.GetRequiredService<IMembershipCalculator>()
            .GetMembershipSnapshotAsync(userId)).IsVolunteerMember;

        var entries = new List<ThingsToDoEntry> { await ProfileEntryAsync(services, localizer, profile, userId) };

        if (!isVolunteerMember)
        {
            var review = await services.GetRequiredService<IConsentService>().GetConsentReviewAsync(userId);
            entries.Add(ConsentCheckEntry(localizer, profile is not null && review.Status == ConsentCheckStatus.Cleared));
        }

        var medical = await MedicalEntryAsync(services, localizer, logger, profile, userId);
        if (medical is not null)
        {
            entries.Add(medical);
        }

        return entries;
    }

    private static async ValueTask<ThingsToDoEntry> ProfileEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        ProfileInfo? profile,
        Guid userId)
    {
        var shiftUser = await services.GetRequiredService<IShiftView>().GetUserAsync(userId);
        var percent = ProfileCompletion.ComputePercent(profile, shiftUser.TagPreferences.Count > 0);

        // Hidden/derived required fields can cap real-user completion in the
        // 90–95% range. Treat 80% as "complete enough" so the nudge stops
        // shouting at people who can't push it higher.
        var complete = percent >= 80;

        return new ThingsToDoEntry("profile", localizer["Todo_Profile_Title"].Value, "fa-solid fa-user",
            Controller: "Profile", Action: "Edit", Weight: 10)
        {
            Description = complete
                ? localizer["Todo_Profile_Done"].Value
                : string.Format(CultureInfo.CurrentCulture,
                    localizer["Dashboard_ProfileCompletionPercent"].Value, percent),
            IsDone = complete,
            ActionText = localizer["Todo_Profile_Action"].Value,
            PercentComplete = complete ? null : percent,
        };
    }

    private static ThingsToDoEntry ConsentCheckEntry(IStringLocalizer<SharedResource> localizer, bool cleared)
    {
        return new ThingsToDoEntry("consent-check", localizer["Todo_ConsentCheck_Title"].Value,
            "fa-solid fa-clipboard-check", Weight: 30)
        {
            Description = cleared
                ? localizer["Todo_ConsentCheck_Done"].Value
                : localizer["Todo_ConsentCheck_Pending"].Value,
            IsDone = cleared,
        };
    }

    /// <summary>
    /// Offers the optional medical-conditions form while the member's dietary nudge is open (no
    /// dietary preference on Cantina's record) and they have no medical conditions on file: the
    /// window the combined dietary-and-medical form had before commit 10. MedicalConditions has no
    /// "answered" marker, so tying it to the dietary answer keeps it from nagging members with
    /// nothing to declare.
    /// </summary>
    private static async ValueTask<ThingsToDoEntry?> MedicalEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        ILogger logger,
        ProfileInfo? profile,
        Guid userId)
    {
        if (!string.IsNullOrEmpty(profile?.MedicalConditions))
        {
            return null;
        }

        try
        {
            var dietary = await services.GetRequiredService<ICantinaService>().GetDietaryProfileAsync(userId);
            if (dietary?.HasPreference == true)
            {
                return null;
            }

            return new ThingsToDoEntry("medical", localizer["Todo_Medical_Title"].Value,
                "fa-solid fa-notes-medical", Controller: "Profile", Action: "Medical", Weight: 51)
            {
                Description = localizer["Todo_Medical_Pending"].Value,
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check the medical prompt for user {UserId}", userId);
            return null;
        }
    }
}

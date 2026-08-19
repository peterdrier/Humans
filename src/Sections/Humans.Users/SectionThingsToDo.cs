using System.Globalization;
using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Humans.Users;

/// <summary>
/// Users' entries on the member dashboard's things-to-do list: profile completion, the
/// consent-check clearance non-volunteers wait on, and the dietary/medical nudge.
/// Spec for the last one: Docs/features/dietary-medical-nudge.md (US-35.5).
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
        var profile = (await services.GetRequiredService<IUserServiceRead>().GetUserInfoAsync(userId))?.Profile;
        var isVolunteerMember = (await services.GetRequiredService<IMembershipCalculatorRead>()
            .GetMembershipSnapshotAsync(userId)).IsVolunteerMember;

        var entries = new List<ThingsToDoEntry> { await ProfileEntryAsync(services, localizer, profile, userId) };

        if (!isVolunteerMember)
        {
            entries.Add(ConsentCheckEntry(localizer, profile));
        }

        var dietary = await DietaryEntryAsync(services, localizer, logger, profile, userId);
        if (dietary is not null)
        {
            entries.Add(dietary);
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

    private static ThingsToDoEntry ConsentCheckEntry(IStringLocalizer<SharedResource> localizer, ProfileInfo? profile)
    {
        var cleared = profile?.ConsentCheckStatus == ConsentCheckStatus.Cleared;

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
    /// Fires whenever DietaryPreference is empty. Copy varies by whether the user has an
    /// active qualifying signup; the entry is the same Key either way.
    /// </summary>
    private static async ValueTask<ThingsToDoEntry?> DietaryEntryAsync(
        IServiceProvider services,
        IStringLocalizer<SharedResource> localizer,
        ILogger logger,
        ProfileInfo? profile,
        Guid userId)
    {
        if (!string.IsNullOrEmpty(profile?.DietaryPreference))
        {
            return null;
        }

        try
        {
            var hasQualifyingSignup = await services.GetRequiredService<IShiftManagementServiceRead>()
                .HasQualifyingCantinaSignupAsync(userId);
            var descriptionKey = hasQualifyingSignup
                ? "Todo_DietaryMedical_Pending"
                : "Todo_DietaryMedical_NoShift_Pending";

            return new ThingsToDoEntry("dietary-medical", localizer["Todo_DietaryMedical_Title"].Value,
                "fa-solid fa-utensils", Controller: "Profile", Action: "DietaryMedical", Weight: 50)
            {
                Description = localizer[descriptionKey].Value,
                ActionText = localizer["Todo_DietaryMedical_Action"].Value,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check dietary/medical nudge for user {UserId}", userId);
            return null;
        }
    }
}

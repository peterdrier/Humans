using System.Security.Claims;
using Humans.Base;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Shifts;

/// <summary>
/// Shifts' entry on the member dashboard's things-to-do list: the shift preferences a
/// volunteer who already signed up still has to fill in. No signups, no entry.
/// </summary>
internal sealed class SectionThingsToDo : ISectionThingsToDo
{
    public async ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !await HasSignupsAsync(services, userId))
        {
            return [];
        }

        var needsShiftInfo = await NeedsShiftInfoAsync(services, userId);
        var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();

        return
        [
            new ThingsToDoEntry("shift-info", localizer["Todo_ShiftInfo_Title"].Value,
                "fa-solid fa-calendar-check", Controller: "ShiftProfile", Action: "ShiftInfo", Weight: 40)
            {
                Description = needsShiftInfo
                    ? localizer["Todo_ShiftInfo_Pending"].Value
                    : localizer["Todo_ShiftInfo_Done"].Value,
                IsDone = !needsShiftInfo,
                ActionText = localizer["Todo_ShiftInfo_Action"].Value,
            }
        ];
    }

    /// <summary>
    /// A pending signup, or a confirmed one that has not ended yet, while browsing is open —
    /// the same commitment the dashboard's shift cards render.
    /// </summary>
    private static async ValueTask<bool> HasSignupsAsync(IServiceProvider services, Guid userId)
    {
        var activeEvent = await services.GetRequiredService<IBurnSettingsService>().GetActiveAsync();
        if (activeEvent is null || !activeEvent.IsShiftBrowsingOpen)
        {
            return false;
        }

        var now = services.GetRequiredService<IClock>().GetCurrentInstant();
        var signups = (await services.GetRequiredService<IShiftView>().GetUserAsync(userId)).Signups;

        return signups.Any(s => s.Status == SignupStatus.Pending)
               || signups.Any(s => s.Status == SignupStatus.Confirmed && s.AbsoluteEnd > now);
    }

    private static async ValueTask<bool> NeedsShiftInfoAsync(IServiceProvider services, Guid userId)
    {
        try
        {
            var profile = await services.GetRequiredService<IShiftVolunteerProfiles>().GetShiftProfileAsync(userId);
            return profile is null || profile.IsEmpty;
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<SectionThingsToDo>>()
                .LogError(ex, "Failed to check shift profile for ThingsToDo component, user {UserId}", userId);
            return false;
        }
    }
}

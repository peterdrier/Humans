using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Helpers;

// T-09 (issue #720): the per-user voluntell search loop used to issue two
// DB calls per candidate (GetShiftProfileAsync + IShiftSignupService.GetByUserAsync).
// It now reads from the cached IShiftView via a single bulk GetUsersAsync —
// cache hits complete synchronously via ValueTask. MedicalConditions are
// redacted at the projection layer here so the shared cached view is never
// mutated.

public enum VolunteerSearchBuildStatus
{
    Success,
    EmptyQuery,
    NotFound,
}

public sealed record VolunteerSearchBuildResult(
    VolunteerSearchBuildStatus Status,
    IReadOnlyList<VolunteerSearchResult> Results)
{
    public static VolunteerSearchBuildResult EmptyQuery { get; } =
        new(VolunteerSearchBuildStatus.EmptyQuery, []);

    public static VolunteerSearchBuildResult NotFound { get; } =
        new(VolunteerSearchBuildStatus.NotFound, []);

    public static VolunteerSearchBuildResult Success(IReadOnlyList<VolunteerSearchResult> results) =>
        new(VolunteerSearchBuildStatus.Success, results);
}

public static class ShiftVolunteerSearchBuilder
{
    public static async Task<VolunteerSearchBuildResult> BuildForShiftAsync(
        Shift? shift,
        string? query,
        Func<Task<EventSettings?>> getActiveEventSettings,
        bool canViewMedical,
        UserManager<User> userManager,
        IShiftView shiftView,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService)
    {
        if (!query.HasSearchTerm())
            return VolunteerSearchBuildResult.EmptyQuery;

        if (shift is null)
            return VolunteerSearchBuildResult.NotFound;

        var activeEvent = await getActiveEventSettings();
        var eventSettings = shift.Rota.EventSettings;

        var results = await BuildAsync(
            shift,
            query.Trim(),
            eventSettings,
            activeEvent,
            canViewMedical,
            userManager,
            shiftView,
            signupService,
            availabilityService);

        return VolunteerSearchBuildResult.Success(results);
    }

    public static async Task<List<VolunteerSearchResult>> BuildAsync(
        Shift shift,
        string query,
        EventSettings eventSettings,
        EventSettings? activeEvent,
        bool canViewMedical,
        UserManager<User> userManager,
        IShiftView shiftView,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService)
    {
        var shiftStart = shift.GetAbsoluteStart(eventSettings);
        var shiftEnd = shift.GetAbsoluteEnd(eventSettings);

        var users = await userManager.Users
            .Where(u => EF.Functions.ILike(u.DisplayName, "%" + query + "%"))
            .OrderBy(u => u.DisplayName)
            .Take(10)
            .ToListAsync();

        var poolVolunteers = await availabilityService.GetAvailableForDayAsync(eventSettings.Id, shift.DayOffset);
        var poolUserIds = poolVolunteers.Select(p => p.UserId).ToHashSet();

        // Bulk-fetch the cached view for every candidate user — replaces the
        // per-user GetShiftProfileAsync + GetByUserAsync round trips with one
        // cache-friendly call (T-09, issue #720).
        var userIds = users.Select(u => u.Id).ToList();
        var views = await shiftView.GetUsersAsync(userIds);

        // The cached ShiftUserView.Signups is scoped to the currently active
        // event (ShiftViewService.GetUserAsync). When the target shift belongs
        // to a different event (e.g. admin searching a past/future event's
        // shift), fall back to a per-user signup query for that event so
        // BookedShiftCount/HasOverlap stay accurate (Codex P2, PR #579).
        var targetIsActive = activeEvent is not null && eventSettings.Id == activeEvent.Id;
        Dictionary<Guid, IReadOnlyList<ShiftSignup>>? targetEventSignups = null;
        if (!targetIsActive)
        {
            targetEventSignups = new Dictionary<Guid, IReadOnlyList<ShiftSignup>>(userIds.Count);
            foreach (var id in userIds)
                targetEventSignups[id] = await signupService.GetByUserAsync(id, eventSettings.Id);
        }

        var results = new List<VolunteerSearchResult>();
        foreach (var user in users)
        {
            var view = views[user.Id];
            var profile = view.Profile;
            var signupsForEvent = targetIsActive
                ? view.Signups
                : targetEventSignups![user.Id];
            var confirmedSignups = signupsForEvent
                .Where(s => s.Status == SignupStatus.Confirmed
                    && s.Shift?.Rota?.EventSettingsId == eventSettings.Id)
                .ToList();

            var hasOverlap = confirmedSignups.Any(signup =>
            {
                var signupStart = signup.Shift.GetAbsoluteStart(eventSettings);
                var signupEnd = signup.Shift.GetAbsoluteEnd(eventSettings);
                return shiftStart < signupEnd && shiftEnd > signupStart;
            });

            results.Add(new VolunteerSearchResult
            {
                UserId = user.Id,
                DisplayName = user.DisplayName,
                Skills = profile?.Skills ?? [],
                Quirks = profile?.Quirks ?? [],
                Languages = profile?.Languages ?? [],
                DietaryPreference = profile?.DietaryPreference,
                BookedShiftCount = confirmedSignups.Count,
                HasOverlap = hasOverlap,
                IsInPool = poolUserIds.Contains(user.Id),
                // canViewMedical gates MedicalConditions here so the shared
                // cached view's Profile is never mutated (would poison the cache).
                MedicalConditions = canViewMedical ? profile?.MedicalConditions : null
            });
        }

        return results;
    }
}

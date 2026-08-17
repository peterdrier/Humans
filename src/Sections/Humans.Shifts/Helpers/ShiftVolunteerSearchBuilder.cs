using Humans.Users.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Domain.Enums;
using Humans.UI.Extensions;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Application;

namespace Humans.Shifts.Helpers;

// T-09 (issue #720): the per-user voluntell search loop used to issue two
// DB calls per candidate (GetShiftProfileAsync + IShiftSignupService.GetByUserAsync).
// It now reads from the cached IShiftRowView via a single bulk GetUsersAsync —
// cache hits complete synchronously via ValueTask. MedicalConditions are
// redacted at the projection layer here so the shared cached view is never
// mutated.

internal enum VolunteerSearchBuildStatus
{
    Success,
    EmptyQuery,
    NotFound,
}

internal sealed record VolunteerSearchBuildResult(
    VolunteerSearchBuildStatus Status,
    IReadOnlyList<VolunteerSearchResult> Results)
{
    internal static VolunteerSearchBuildResult EmptyQuery { get; } =
        new(VolunteerSearchBuildStatus.EmptyQuery, []);

    internal static VolunteerSearchBuildResult NotFound { get; } =
        new(VolunteerSearchBuildStatus.NotFound, []);

    internal static VolunteerSearchBuildResult Success(IReadOnlyList<VolunteerSearchResult> results) =>
        new(VolunteerSearchBuildStatus.Success, results);
}

internal sealed class ShiftVolunteerSearchBuilder(
    IBurnSettingsService burnSettings,
    IUserServiceRead userService,
    IShiftRowView shiftView,
    IShiftSignupService signupService,
    IVolunteerTrackingService volunteerTrackingService)
{
    public async Task<VolunteerSearchBuildResult> BuildForShiftAsync(
        Shift? shift,
        string? query,
        bool canViewMedical)
    {
        if (!query.HasSearchTerm())
            return VolunteerSearchBuildResult.EmptyQuery;

        if (shift is null)
            return VolunteerSearchBuildResult.NotFound;

        // Two distinct burns, deliberately not collapsed: the target shift's own
        // burn (drives its calendar) and the currently active one (decides whether
        // the cached, active-scoped ShiftUserView.Signups can be reused). Admins
        // search shifts in past/future cycles, so these differ.
        var activeEvent = await burnSettings.GetActiveAsync();
        var eventSettings = activeEvent?.Id == shift.Rota.EventSettingsId
            ? activeEvent
            : await burnSettings.GetByIdAsync(shift.Rota.EventSettingsId);

        if (eventSettings is null)
            return VolunteerSearchBuildResult.NotFound;

        var results = await BuildAsync(
            shift,
            query.Trim(),
            eventSettings,
            activeEvent,
            canViewMedical);

        return VolunteerSearchBuildResult.Success(results);
    }

    private async Task<List<VolunteerSearchResult>> BuildAsync(
        Shift shift,
        string query,
        BurnSettingsInfo eventSettings,
        BurnSettingsInfo? activeEvent,
        bool canViewMedical)
    {
        var shiftStart = shift.GetAbsoluteStart(eventSettings);
        var shiftEnd = shift.GetAbsoluteEnd(eventSettings);

        // Request the full match set (the service short-circuits at `limit`, so a small limit
        // returns an arbitrary subset in non-deterministic cache order) and rank by relevance so
        // the closest name match leads. Uncapped — people must be findable (Codex P2, PR #638);
        // cache is ~500 users so the full sort is cheap.
        var users = (await userService.SearchUsersAsync(query, PersonSearchFields.Name, limit: int.MaxValue))
            .OrderByRelevance()
            .ToList();

        var poolVolunteers = await volunteerTrackingService.GetAvailableForDayAsync(eventSettings.Id, shift.DayOffset);
        var poolUserIds = poolVolunteers.Select(p => p.UserId).ToHashSet();

        // Bulk-fetch the cached view for every candidate user — replaces the
        // per-user GetShiftProfileAsync + GetByUserAsync round trips with one
        // cache-friendly call (T-09, issue #720).
        var userIds = users.Select(u => u.UserId).ToList();
        var views = await shiftView.GetUsersAsync(userIds);

        // Dietary + medical moved to Profile — read them from the cached UserInfo.
        // (Skills/Quirks/Languages still come from the ShiftUserView's VEP.)
        var userInfos = await userService.GetUserInfosAsync(userIds);

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
            var view = views[user.UserId];
            var profile = view.Profile;
            userInfos.TryGetValue(user.UserId, out var info);
            var personProfile = info?.Profile;
            var signupsForEvent = targetIsActive
                ? view.Signups
                : targetEventSignups![user.UserId];
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
                UserId = user.UserId,
                DisplayName = user.BurnerName,
                Skills = profile?.Skills ?? [],
                Quirks = profile?.Quirks ?? [],
                Languages = profile?.Languages ?? [],
                DietaryPreference = personProfile?.DietaryPreference,
                BookedShiftCount = confirmedSignups.Count,
                HasOverlap = hasOverlap,
                IsInPool = poolUserIds.Contains(user.UserId),
                // canViewMedical gates MedicalConditions — without the MedicalDataViewer
                // policy the field is never surfaced (UserInfo carries it, the view withholds it).
                MedicalConditions = canViewMedical ? personProfile?.MedicalConditions : null
            });
        }

        return results;
    }
}

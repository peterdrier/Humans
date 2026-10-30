using Humans.Cantina.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Base.Extensions;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Helpers;

// Medical conditions never enter the search projection: the shift admin page reveals them per person, audited.

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
    IUserService userService,
    ICantinaService cantina,
    IShiftRowView shiftView,
    IShiftSignupService signupService,
    IVolunteerTrackingManagement volunteerTrackingService)
{
    public async Task<VolunteerSearchBuildResult> BuildForShiftAsync(
        ShiftWithRota? shift,
        string? query,
        CancellationToken ct = default)
    {
        if (!query.HasSearchTerm())
            return VolunteerSearchBuildResult.EmptyQuery;

        if (shift is null)
            return VolunteerSearchBuildResult.NotFound;

        // Two burns, deliberately not collapsed: the shift's own drives its calendar; the active one decides whether
        // the active-scoped cached signups can be reused. Admins search past and future cycles, so they differ.
        var activeEvent = await burnSettings.GetActiveAsync(ct);
        var eventSettings = activeEvent?.Id == shift.Rota.EventSettingsId
            ? activeEvent
            : await burnSettings.GetByIdAsync(shift.Rota.EventSettingsId, ct);

        if (eventSettings is null)
            return VolunteerSearchBuildResult.NotFound;

        var results = await BuildAsync(
            shift,
            query.Trim(),
            eventSettings,
            activeEvent,
            ct);

        return VolunteerSearchBuildResult.Success(results);
    }

    private async Task<List<VolunteerSearchResult>> BuildAsync(
        ShiftWithRota shift,
        string query,
        BurnSettingsInfo eventSettings,
        BurnSettingsInfo? activeEvent,
        CancellationToken ct)
    {
        var shiftStart = shift.Shift.GetAbsoluteStart(eventSettings);
        var shiftEnd = shift.Shift.GetAbsoluteEnd(eventSettings);

        // Uncapped: the service stops at `limit` in arbitrary cache order, and people must be findable. The cache is
        // small, so ranking the full set is cheap.
        var users = (await userService.SearchUsersAsync(query, PersonSearchFields.Name, limit: int.MaxValue, ct: ct))
            .OrderByRelevance()
            .ToList();

        var poolVolunteers = await volunteerTrackingService.GetAvailableForDayAsync(eventSettings.Id, shift.Shift.DayOffset, ct);
        var poolUserIds = poolVolunteers.Select(p => p.UserId).ToHashSet();

        var userIds = users.Select(u => u.UserId).ToList();
        var views = await shiftView.GetUsersAsync(userIds, ct);

        // Dietary from Cantina; skills, quirks and languages from the shift view.
        var diets = await cantina.GetDietaryProfilesAsync(userIds, ct);

        // The cached signups cover only the active event; for another event's shift, query that event's signups so
        // BookedShiftCount and HasOverlap stay accurate.
        var targetIsActive = activeEvent is not null && eventSettings.Id == activeEvent.Id;
        Dictionary<Guid, IReadOnlyList<ShiftSignupSummary>>? targetEventSignups = null;
        if (!targetIsActive)
        {
            targetEventSignups = new Dictionary<Guid, IReadOnlyList<ShiftSignupSummary>>(userIds.Count);
            foreach (var id in userIds)
                targetEventSignups[id] = await signupService.GetByUserAsync(id, eventSettings.Id, ct);
        }

        var results = new List<VolunteerSearchResult>();
        foreach (var user in users)
        {
            var view = views[user.UserId];
            var profile = view.Profile;
            diets.TryGetValue(user.UserId, out var diet);
            var signupsForEvent = targetIsActive
                ? view.ToSummary().Signups
                : targetEventSignups![user.UserId];
            var confirmedSignups = signupsForEvent
                .Where(s => s.Status == SignupStatus.Confirmed
                    && s.EventSettingsId == eventSettings.Id)
                .ToList();

            var hasOverlap = confirmedSignups.Any(signup =>
                shiftStart < signup.AbsoluteEnd && shiftEnd > signup.AbsoluteStart);

            results.Add(new VolunteerSearchResult
            {
                UserId = user.UserId,
                DisplayName = user.BurnerName,
                Skills = profile?.Skills ?? [],
                Quirks = profile?.Quirks ?? [],
                Languages = profile?.Languages ?? [],
                DietaryPreference = diet?.DietaryPreference,
                BookedShiftCount = confirmedSignups.Count,
                HasOverlap = hasOverlap,
                IsInPool = poolUserIds.Contains(user.UserId),
            });
        }

        return results;
    }
}

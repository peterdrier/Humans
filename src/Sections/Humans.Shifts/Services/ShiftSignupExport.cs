using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Shifts.Services;

internal sealed class ShiftSignupExport(
    IShiftManagementRepository repo,
    IVolunteerTrackingRepository trackingRepo,
    EventCalendarResolver calendarResolver,
    IUserService users,
    IServiceProvider serviceProvider) : IApplicationService
{
    internal const string ShiftSignups = "ShiftSignups";
    internal const string VolunteerEventProfiles = "VolunteerEventProfiles";
    internal const string GeneralAvailability = "GeneralAvailability";
    internal const string VolunteerBuildStatuses = "VolunteerBuildStatuses";
    internal const string ShiftTagPreferences = "ShiftTagPreferences";

    // Lazy, to break the DI cycle with TeamService.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var signups = (await repo.GetForUsersAsync([userId], ct: ct))
            .OrderByDescending(ss => ss.CreatedAt)
            .ToList();

        // Includes deactivated teams, so historical signups still resolve a name.
        var referencedTeamIds = signups
            .Select(ss => ss.Shift.Rota.TeamId)
            .Distinct()
            .ToList();
        var teamsByIdLookup = await TeamService.GetTeamsAsync(ct);
        var teamNamesById = referencedTeamIds
            .Where(teamsByIdLookup.ContainsKey)
            .ToDictionary(id => id, id => teamsByIdLookup[id].Name);

        var volunteerEventProfile = await repo.GetVolunteerEventProfileAsync(userId, ct);
        IReadOnlyList<VolunteerEventProfile> volunteerEventProfiles =
            volunteerEventProfile is null ? [] : [volunteerEventProfile];
        var generalAvailability = await trackingRepo.GetAvailabilityForUserAsync(userId, ct: ct);
        var tagPreferences = await repo.GetVolunteerTagPreferencesForUsersAsync([userId], ct);
        var exportUserIds = (await users.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];
        var buildStatuses = await trackingRepo.GetBuildStatusesForEventAsync(null, exportUserIds, ct);

        var distinctEventSettingsIds = signups.Select(ss => ss.Shift.Rota.EventSettingsId)
            .Concat(generalAvailability.Select(ga => ga.EventSettingsId))
            .Concat(buildStatuses.Select(bs => bs.EventSettingsId))
            .Distinct();
        var eventNamesById = new Dictionary<Guid, string>();
        foreach (var id in distinctEventSettingsIds)
        {
            var calendar = await calendarResolver.GetAsync(id, ct);
            if (calendar is not null)
                eventNamesById[id] = calendar.EventName;
        }

        var signupSlice = new UserDataSlice(ShiftSignups, signups.Select(ss => new
        {
            EventName = eventNamesById.TryGetValue(ss.Shift.Rota.EventSettingsId, out var signupEventName) ? signupEventName : string.Empty,
            Department = teamNamesById.TryGetValue(ss.Shift.Rota.TeamId, out var teamName) ? teamName : null,
            RotaName = ss.Shift.Rota.Name,
            ss.Shift.DayOffset,
            ss.Shift.IsAllDay,
            ss.Status,
            ss.Enrolled,
            ss.StatusReason,
            CreatedAt = ss.CreatedAt.ToIso8601(),
            UpdatedAt = ss.UpdatedAt.ToIso8601(),
            ReviewedAt = ss.ReviewedAt.ToIso8601()
        }).ToList());

        // Dietary and medical are exported by Profiles; this slice is shift-matching data only.
        var vepSlice = new UserDataSlice(VolunteerEventProfiles, volunteerEventProfiles.Select(vep => new
        {
            vep.Skills,
            vep.Quirks,
            vep.Languages,
            CreatedAt = vep.CreatedAt.ToIso8601(),
            UpdatedAt = vep.UpdatedAt.ToIso8601()
        }).ToList());

        var availabilitySlice = new UserDataSlice(GeneralAvailability, generalAvailability.Select(ga => new
        {
            EventName = eventNamesById.TryGetValue(ga.EventSettingsId, out var eventName) ? eventName : string.Empty,
            ga.AvailableDayOffsets,
            UpdatedAt = ga.UpdatedAt.ToIso8601()
        }).ToList());

        var tagPreferenceSlice = new UserDataSlice(ShiftTagPreferences, tagPreferences.Select(vtp => new
        {
            TagName = vtp.ShiftTag.Name
        }).ToList());

        var buildStatusSlice = new UserDataSlice(VolunteerBuildStatuses, buildStatuses.Select(bs => new
        {
            bs.EventSettingsId,
            EventName = eventNamesById.GetValueOrDefault(bs.EventSettingsId, string.Empty),
            BarrioSetupStartDate = bs.BarrioSetupStartDate.ToInvariantDate(),
            bs.Notes,
            SetAt = bs.SetAt.ToIso8601(),
            DayOffs = bs.DayOffs.Select(day => new
            {
                day.DayOffset,
                day.Reason,
                MarkedAt = day.MarkedAt.ToIso8601()
            }).ToList()
        }).ToList());

        return [signupSlice, vepSlice, availabilitySlice, tagPreferenceSlice, buildStatusSlice];
    }
}

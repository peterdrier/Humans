using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Helpers;

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
        IShiftManagementService shiftManagementService,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService)
    {
        if (!query.HasSearchTerm())
            return VolunteerSearchBuildResult.EmptyQuery;

        if (shift is null)
            return VolunteerSearchBuildResult.NotFound;

        var eventSettings = shift.Rota.EventSettings ?? await getActiveEventSettings();
        if (eventSettings is null)
            return VolunteerSearchBuildResult.NotFound;

        var results = await BuildAsync(
            shift,
            query.Trim(),
            eventSettings,
            canViewMedical,
            userManager,
            shiftManagementService,
            signupService,
            availabilityService);

        return VolunteerSearchBuildResult.Success(results);
    }

    public static async Task<List<VolunteerSearchResult>> BuildAsync(
        Shift shift,
        string query,
        EventSettings eventSettings,
        bool canViewMedical,
        UserManager<User> userManager,
        IShiftManagementService shiftManagementService,
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

        var results = new List<VolunteerSearchResult>();
        foreach (var user in users)
        {
            var profile = await shiftManagementService.GetShiftProfileAsync(user.Id, includeMedical: canViewMedical);
            var userSignups = await signupService.GetByUserAsync(user.Id, eventSettings.Id);
            var confirmedSignups = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();

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
                MedicalConditions = profile?.MedicalConditions
            });
        }

        return results;
    }
}

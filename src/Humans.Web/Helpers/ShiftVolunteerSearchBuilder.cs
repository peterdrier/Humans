using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Helpers;

public static class ShiftVolunteerSearchBuilder
{
    public static async Task<List<VolunteerSearchResult>> BuildAsync(
        Shift shift,
        string query,
        EventSettings eventSettings,
        bool canViewMedical,
        UserManager<User> userManager,
        IProfileService profileService,
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
            var profile = await profileService.GetShiftProfileAsync(user.Id, includeMedical: canViewMedical);
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

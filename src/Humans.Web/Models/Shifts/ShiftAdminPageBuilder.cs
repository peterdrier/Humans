using Humans.Application;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

public sealed record ShiftAdminPageRequest(
    TeamInfo Department,
    EventSettings EventSettings,
    bool CanManage,
    bool CanApprove,
    bool CanViewMedical,
    bool IncompleteOnboarding,
    Instant Now);

public sealed class ShiftAdminPageBuilder(
    IShiftManagementService shiftManagement,
    IShiftSignupService signupService,
    IUserServiceRead userService,
    ITeamServiceRead teamService)
{
    public async Task<ShiftAdminViewModel> BuildAsync(ShiftAdminPageRequest request)
    {
        var rotas = await shiftManagement.GetRotasByDepartmentAsync(request.Department.Id, request.EventSettings.Id);
        var staffing = await BuildStaffingSummaryAsync(rotas, request.IncompleteOnboarding);
        var allUserIds = GetRelevantUserIds(rotas, staffing.PendingSignups);
        var profileDict = await LoadProfilesAsync(allUserIds, request.CanViewMedical);
        var userLookup = allUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(allUserIds);
        var allDepartments = await LoadTransferDepartmentsAsync(request.Department);
        var allTags = await shiftManagement.GetTagsAsync();
        var staffingData = await shiftManagement.GetStaffingDataAsync(request.EventSettings.Id, request.Department.Id);
        var staffingHours = await shiftManagement.GetStaffingHoursAsync(request.EventSettings.Id, request.Department.Id);

        return new ShiftAdminViewModel
        {
            Department = request.Department,
            EventSettings = request.EventSettings,
            Rotas = rotas.ToList(),
            PendingSignups = staffing.PendingSignups,
            TotalSlots = staffing.TotalSlots,
            ConfirmedCount = staffing.ConfirmedCount,
            CanManageShifts = request.CanManage,
            CanApproveSignups = request.CanApprove,
            VolunteerProfiles = profileDict,
            Users = userLookup,
            CanViewMedical = request.CanViewMedical,
            StaffingData = staffingData.ToList(),
            StaffingHours = staffingHours.ToList(),
            Now = request.Now,
            AllDepartments = allDepartments,
            AllTags = allTags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            IncompleteOnboardingFilter = request.IncompleteOnboarding
        };
    }

    private async Task<(List<ShiftSignup> PendingSignups, int TotalSlots, int ConfirmedCount)> BuildStaffingSummaryAsync(
        IReadOnlyList<Rota> rotas,
        bool incompleteOnboarding)
    {
        var pendingSignups = new List<ShiftSignup>();
        var totalSlots = 0;
        var confirmedCount = 0;

        foreach (var rota in rotas)
        {
            foreach (var shift in rota.Shifts)
            {
                totalSlots += shift.MaxVolunteers;
                var shiftSignups = await signupService.GetByShiftAsync(shift.Id);
                confirmedCount += shiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
                pendingSignups.AddRange(shiftSignups.Where(s => s.Status == SignupStatus.Pending));
            }
        }

        if (incompleteOnboarding)
            pendingSignups = (await signupService.FilterToIncompleteOnboardingAsync(pendingSignups)).ToList();

        return (pendingSignups, totalSlots, confirmedCount);
    }

    private async Task<Dictionary<Guid, VolunteerBadgesViewModel>> LoadProfilesAsync(
        IReadOnlyList<Guid> userIds,
        bool canViewMedical)
    {
        var result = new Dictionary<Guid, VolunteerBadgesViewModel>();
        if (userIds.Count == 0) return result;

        // Skills/Quirks/Languages come from the shift profile (VEP); dietary +
        // medical from the person's Profile (cached UserInfo). Medical is gated
        // here — populated only when the viewer holds MedicalDataViewer.
        var userInfos = await userService.GetUserInfosAsync(userIds);
        foreach (var uid in userIds)
        {
            var vep = await shiftManagement.GetShiftProfileAsync(uid);
            userInfos.TryGetValue(uid, out var info);
            var person = info?.Profile;
            if (vep is null && person is null) continue;

            result[uid] = new VolunteerBadgesViewModel(
                Skills: vep?.Skills ?? [],
                Quirks: vep?.Quirks ?? [],
                Languages: vep?.Languages ?? [],
                DietaryPreference: person?.DietaryPreference,
                MedicalConditions: canViewMedical ? person?.MedicalConditions : null,
                ShowMedical: canViewMedical);
        }

        return result;
    }

    private async Task<List<DepartmentOption>> LoadTransferDepartmentsAsync(TeamInfo currentDepartment)
    {
        return (await teamService.GetTeamsAsync()).Values
            .Where(t => t.ParentTeamId is null
                        && t.SystemTeamType == SystemTeamType.None
                        && t.Id != currentDepartment.Id)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new DepartmentOption { TeamId = t.Id, Name = t.Name })
            .ToList();
    }

    private static IReadOnlyList<Guid> GetRelevantUserIds(
        IReadOnlyList<Rota> rotas,
        IReadOnlyList<ShiftSignup> pendingSignups) =>
        rotas.SelectMany(r => r.Shifts)
            .SelectMany(s => s.ShiftSignups)
            .Select(su => su.UserId)
            .Concat(pendingSignups.Select(p => p.UserId))
            .Distinct()
            .ToList();
}

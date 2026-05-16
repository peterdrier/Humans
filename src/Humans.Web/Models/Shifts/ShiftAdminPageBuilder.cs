using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models.Shifts;

public sealed record ShiftAdminPageRequest(
    Team Department,
    EventSettings EventSettings,
    bool CanManage,
    bool CanApprove,
    bool CanViewMedical,
    bool IncompleteOnboarding,
    Instant Now);

public sealed class ShiftAdminPageBuilder
{
    private readonly IShiftManagementService _shiftManagement;
    private readonly IShiftSignupService _signupService;
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;

    public ShiftAdminPageBuilder(
        IShiftManagementService shiftManagement,
        IShiftSignupService signupService,
        IUserService userService,
        ITeamService teamService)
    {
        _shiftManagement = shiftManagement;
        _signupService = signupService;
        _userService = userService;
        _teamService = teamService;
    }

    public async Task<ShiftAdminViewModel> BuildAsync(ShiftAdminPageRequest request)
    {
        var rotas = await _shiftManagement.GetRotasByDepartmentAsync(request.Department.Id, request.EventSettings.Id);
        var staffing = await BuildStaffingSummaryAsync(rotas, request.IncompleteOnboarding);
        var allUserIds = GetRelevantUserIds(rotas, staffing.PendingSignups);
        var profileDict = await LoadProfilesAsync(allUserIds, request.CanViewMedical);
        var userLookup = allUserIds.Count == 0
            ? new Dictionary<Guid, User>()
            : await _userService.GetByIdsAsync(allUserIds);
        var allDepartments = await LoadTransferDepartmentsAsync(request.Department);
        var allTags = await _shiftManagement.GetTagsAsync();
        var staffingData = await _shiftManagement.GetStaffingDataAsync(request.EventSettings.Id, request.Department.Id);
        var staffingHours = await _shiftManagement.GetStaffingHoursAsync(request.EventSettings.Id, request.Department.Id);

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
                var shiftSignups = await _signupService.GetByShiftAsync(shift.Id);
                confirmedCount += shiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
                pendingSignups.AddRange(shiftSignups.Where(s => s.Status == SignupStatus.Pending));
            }
        }

        if (incompleteOnboarding)
            pendingSignups = (await _signupService.FilterToIncompleteOnboardingAsync(pendingSignups)).ToList();

        return (pendingSignups, totalSlots, confirmedCount);
    }

    private async Task<Dictionary<Guid, VolunteerEventProfile>> LoadProfilesAsync(
        IReadOnlyList<Guid> userIds,
        bool canViewMedical)
    {
        var profileDict = new Dictionary<Guid, VolunteerEventProfile>();
        foreach (var uid in userIds)
        {
            var profile = await _shiftManagement.GetShiftProfileAsync(uid, includeMedical: canViewMedical);
            if (profile is not null)
                profileDict[uid] = profile;
        }

        return profileDict;
    }

    private async Task<List<DepartmentOption>> LoadTransferDepartmentsAsync(Team currentDepartment)
    {
        var allTeams = await _teamService.GetAllTeamsAsync();
        return allTeams
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

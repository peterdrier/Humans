using Humans.Cantina.Contracts;
using Humans.Governance.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Humans.Shifts.Services.Dtos;
using Humans.Base.Enums;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Shifts.Models;

internal sealed record ShiftAdminPageRequest(
    TeamInfo Department,
    BurnSettingsInfo EventSettings,
    bool CanManage,
    bool CanApprove,
    bool CanViewMedical,
    bool IncompleteOnboarding,
    Instant Now);

internal sealed class ShiftAdminPageBuilder(
    IShiftManagement shiftManagement,
    IMembershipCalculator membership,
    IUserService userService,
    ICantinaService cantina,
    ITeamService teamService)
{
    public async Task<ShiftAdminViewModel> BuildAsync(ShiftAdminPageRequest request, CancellationToken ct = default)
    {
        var rotas = await shiftManagement.GetRotasByDepartmentAsync(request.Department.Id, request.EventSettings.Id, ct);
        var staffing = await BuildStaffingSummaryAsync(rotas, request.IncompleteOnboarding, ct);
        var allUserIds = GetRelevantUserIds(rotas, staffing.PendingSignups);
        var profileDict = await LoadProfilesAsync(allUserIds, request.CanViewMedical, ct);
        var userLookup = allUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(allUserIds, ct);
        var allDepartments = await LoadTransferDepartmentsAsync(request.Department, ct);
        var allTags = await shiftManagement.GetTagsAsync(ct: ct);
        var staffingSnapshot = await shiftManagement.GetStaffingSnapshotAsync(request.EventSettings.Id, request.Department.Id, ct: ct);

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
            StaffingData = staffingSnapshot.StaffingData.ToList(),
            StaffingHours = staffingSnapshot.StaffingHours.ToList(),
            Now = request.Now,
            AllDepartments = allDepartments,
            AllTags = allTags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            IncompleteOnboardingFilter = request.IncompleteOnboarding
        };
    }

    private async Task<(List<ShiftAdminPendingSignup> PendingSignups, int TotalSlots, int ConfirmedCount)> BuildStaffingSummaryAsync(
        IReadOnlyList<ShiftAdminRota> rotas,
        bool incompleteOnboarding,
        CancellationToken ct)
    {
        var pendingSignups = new List<ShiftAdminPendingSignup>();
        var totalSlots = 0;
        var confirmedCount = 0;

        foreach (var rota in rotas)
        {
            foreach (var shift in rota.Shifts)
            {
                totalSlots += shift.Shift.MaxVolunteers;
                confirmedCount += shift.ConfirmedCount;
                pendingSignups.AddRange(shift.Signups
                    .Where(s => s.Status == SignupStatus.Pending)
                    .Select(s => new ShiftAdminPendingSignup(
                        s.Id, s.UserId, s.SignupBlockId, s.CreatedAt,
                        shift.Shift, shift.ConfirmedCount, rota.Rota.Name)));
            }
        }

        if (incompleteOnboarding)
            pendingSignups = (await FilterToIncompleteOnboardingAsync(pendingSignups, ct)).ToList();

        return (pendingSignups, totalSlots, confirmedCount);
    }

    private async Task<IReadOnlyList<ShiftAdminPendingSignup>> FilterToIncompleteOnboardingAsync(
        IReadOnlyList<ShiftAdminPendingSignup> signups, CancellationToken ct)
    {
        if (signups.Count == 0) return signups;

        var userIds = signups.Select(s => s.UserId).Distinct().ToList();
        var withConsents = await membership.GetUsersWithAllRequiredConsentsForTeamAsync(
            userIds,
            SystemTeamIds.Volunteers, ct);

        return signups.Where(s => !withConsents.Contains(s.UserId)).ToList();
    }

    private async Task<Dictionary<Guid, VolunteerBadgesViewModel>> LoadProfilesAsync(
        IReadOnlyList<Guid> userIds,
        bool canViewMedical, CancellationToken ct)
    {
        var result = new Dictionary<Guid, VolunteerBadgesViewModel>();
        if (userIds.Count == 0) return result;

        // Skills from the shift profile, dietary from Cantina, medical from Profile, gated here on MedicalDataViewer.
        var userInfos = await userService.GetUserInfosAsync(userIds, ct);
        var diets = await cantina.GetDietaryProfilesAsync(userIds, ct);
        foreach (var uid in userIds)
        {
            var vep = await shiftManagement.GetShiftProfileAsync(uid, ct);
            userInfos.TryGetValue(uid, out var info);
            var person = info?.Profile;
            diets.TryGetValue(uid, out var diet);
            if (vep is null && person is null && diet is null) continue;

            result[uid] = new VolunteerBadgesViewModel(
                Skills: vep?.Skills ?? [],
                Quirks: vep?.Quirks ?? [],
                Languages: vep?.Languages ?? [],
                DietaryPreference: diet?.DietaryPreference,
                MedicalConditions: canViewMedical ? person?.MedicalConditions : null,
                ShowMedical: canViewMedical);
        }

        return result;
    }

    private async Task<List<DepartmentOption>> LoadTransferDepartmentsAsync(TeamInfo currentDepartment, CancellationToken ct)
    {
        return (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.ParentTeamId is null
                        && t.SystemTeamType == SystemTeamType.None
                        && t.Id != currentDepartment.Id)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new DepartmentOption { TeamId = t.Id, Name = t.Name })
            .ToList();
    }

    private static IReadOnlyList<Guid> GetRelevantUserIds(
        IReadOnlyList<ShiftAdminRota> rotas,
        IReadOnlyList<ShiftAdminPendingSignup> pendingSignups) =>
        rotas.SelectMany(r => r.Shifts)
            .SelectMany(s => s.Signups)
            .Select(su => su.UserId)
            .Concat(pendingSignups.Select(p => p.UserId))
            .Distinct()
            .ToList();
}

// @e2e: shifts.spec.ts
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Web.Models.Vol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Vol")]
public class VolController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly ILogger<VolController> _logger;
    private readonly IClock _clock;

    public VolController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        ITeamService teamService,
        IProfileService profileService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        ILogger<VolController> logger,
        IClock clock)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _teamService = teamService;
        _profileService = profileService;
        _availabilityService = availabilityService;
        _logger = logger;
        _clock = clock;
    }

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(MyShifts));

    [HttpGet("MyShifts")]
    public async Task<IActionResult> MyShifts()
    {
        try
        {
            var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
            if (currentUserNotFound is not null) return currentUserNotFound;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var signups = await _signupService.GetByUserAsync(user.Id, es.Id);

            var model = new Models.Vol.MyShiftsViewModel
            {
                EventSettings = es,
                Shifts = signups.Select(signup => new Models.Vol.MyShiftsViewModel.MyShiftRow
                {
                    SignupId = signup.Id,
                    DutyTitle = string.IsNullOrWhiteSpace(signup.Shift.Description)
                        ? signup.Shift.Rota.Name
                        : $"{signup.Shift.Rota.Name} — {signup.Shift.Description}",
                    TeamName = signup.Shift.Rota.Team.Name,
                    AbsoluteStart = signup.Shift.GetAbsoluteStart(es),
                    AbsoluteEnd = signup.Shift.GetAbsoluteEnd(es),
                    Status = signup.Status
                }).OrderBy(s => s.AbsoluteStart).ToList()
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading My Shifts");
            throw;
        }
    }

    [HttpPost("Bail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bail(Guid signupId)
    {
        try
        {
            var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
            if (currentUserNotFound is not null) return currentUserNotFound;

            var result = await _signupService.BailAsync(signupId, user.Id, null);

            if (!result.Success)
            {
                SetError(result.Error ?? "Shift bail failed.");
                return RedirectToAction(nameof(MyShifts));
            }

            SetSuccess("Successfully bailed from shift.");
            return RedirectToAction(nameof(MyShifts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bailing from shift {SignupId}", signupId);
            throw;
        }
    }

    [HttpGet("Shifts")]
    public async Task<IActionResult> Shifts(Guid? departmentId, string? fromDate, string? toDate, string? period, bool showFull = false)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var isPrivileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                               (await _shiftMgmt.GetCoordinatorDepartmentIdsAsync(user.Id)).Count > 0;

            var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
            var hasSignups = userSignups.Count > 0;

            if (!es.IsShiftBrowsingOpen && !isPrivileged && !hasSignups)
                return View("BrowsingClosed");

            var userSignupShiftIds = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(s => s.ShiftId)
                .ToHashSet();
            var userSignupStatuses = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .ToDictionary(s => s.ShiftId, s => s.Status);

            // Parse date range filters
            LocalDate? filterFromDate = null;
            LocalDate? filterToDate = null;
            if (!string.IsNullOrEmpty(fromDate) && LocalDatePattern.Iso.Parse(fromDate) is { Success: true } fromResult)
                filterFromDate = fromResult.Value;
            if (!string.IsNullOrEmpty(toDate) && LocalDatePattern.Iso.Parse(toDate) is { Success: true } toResult)
                filterToDate = toResult.Value;

            // Apply period filter — compute date boundaries from EventSettings offsets
            if (!string.IsNullOrEmpty(period) && Enum.TryParse<ShiftPeriod>(period, true, out var parsedPeriod))
            {
                var periodFrom = parsedPeriod switch
                {
                    ShiftPeriod.Build => es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                    ShiftPeriod.Event => es.GateOpeningDate,
                    ShiftPeriod.Strike => es.GateOpeningDate.PlusDays(es.EventEndOffset + 1),
                    _ => es.GateOpeningDate.PlusDays(es.BuildStartOffset)
                };
                var periodTo = parsedPeriod switch
                {
                    ShiftPeriod.Build => es.GateOpeningDate.PlusDays(-1),
                    ShiftPeriod.Event => es.GateOpeningDate.PlusDays(es.EventEndOffset),
                    ShiftPeriod.Strike => es.GateOpeningDate.PlusDays(es.StrikeEndOffset),
                    _ => es.GateOpeningDate.PlusDays(es.StrikeEndOffset)
                };
                filterFromDate ??= periodFrom;
                filterToDate ??= periodTo;
            }

            var browseShifts = await _shiftMgmt.GetBrowseShiftsAsync(
                es.Id, departmentId: departmentId,
                fromDate: filterFromDate, toDate: filterToDate,
                includeAdminOnly: isPrivileged, includeSignups: isPrivileged,
                includeHidden: isPrivileged);

            // Group by department -> rota -> shift
            var departments = browseShifts
                .GroupBy(u => u.Shift.Rota.TeamId)
                .Select(deptGroup =>
                {
                    var firstShift = deptGroup.First().Shift;
                    return new DepartmentShiftGroup
                    {
                        TeamId = firstShift.Rota.TeamId,
                        TeamName = firstShift.Rota.Team.Name,
                        TeamSlug = firstShift.Rota.Team.Slug,
                        Rotas = deptGroup.GroupBy(u => u.Shift.RotaId)
                            .Select(rotaGroup =>
                            {
                                var rota = rotaGroup.First().Shift.Rota;
                                return new RotaShiftGroup
                                {
                                    Rota = rota,
                                    Shifts = rotaGroup.Select(u =>
                                    {
                                        var (start, end, period) = _shiftMgmt.ResolveShiftTimes(u.Shift, es);
                                        return new ShiftDisplayItem
                                        {
                                            Shift = u.Shift,
                                            AbsoluteStart = start,
                                            AbsoluteEnd = end,
                                            Period = period,
                                            ConfirmedCount = u.ConfirmedCount,
                                            RemainingSlots = u.RemainingSlots,
                                            Signups = u.Signups.Select(s => new ShiftSignupInfo(
                                                s.UserId, s.DisplayName, s.Status,
                                                s.HasProfilePicture ? $"/Human/{s.UserId}/Picture" : null))
                                                .ToList()
                                        };
                                    }).OrderBy(s => s.AbsoluteStart).ToList()
                                };
                            }).OrderBy(r => r.Rota.Name, StringComparer.Ordinal).ToList()
                    };
                }).OrderBy(d => d.TeamName, StringComparer.Ordinal).ToList();

            List<DepartmentOption> allDepartments;
            if (!departmentId.HasValue)
            {
                allDepartments = departments
                    .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName }).ToList();
            }
            else
            {
                var depts = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
                allDepartments = depts
                    .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName }).ToList();
            }

            var model = new ShiftBrowserViewModel
            {
                EventSettings = es,
                FilterDepartmentId = departmentId,
                FilterFromDate = fromDate,
                FilterToDate = toDate,
                FilterPeriod = period,
                ShowFullShifts = showFull,
                UserSignupShiftIds = userSignupShiftIds,
                UserSignupStatuses = userSignupStatuses,
                Departments = departments,
                AllDepartments = allDepartments,
                ShowSignups = isPrivileged,
                IsPrivileged = isPrivileged
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading All Shifts page");
            SetError("Failed to load shifts.");
            return RedirectToAction(nameof(MyShifts));
        }
    }

    [HttpPost("SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
            var result = await _signupService.SignUpAsync(user.Id, shiftId, isPrivileged: privileged);

            if (!result.Success)
            {
                SetError(result.Error ?? "Shift signup failed.");
                return RedirectToAction(nameof(Shifts));
            }

            SetSuccess(result.Warning is not null
                ? $"Signed up successfully. Note: {result.Warning}"
                : "Signed up successfully!");

            return RedirectToAction(nameof(Shifts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error signing up for shift {ShiftId}", shiftId);
            SetError("Failed to sign up.");
            return RedirectToAction(nameof(Shifts));
        }
    }

    [HttpGet("Teams")]
    public async Task<IActionResult> Teams()
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var deptTuples = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
            var allTeams = await _teamService.GetAllTeamsAsync();

            var departments = new List<TeamsOverviewViewModel.DepartmentCard>();
            foreach (var (teamId, teamName) in deptTuples)
            {
                var team = allTeams.FirstOrDefault(t => t.Id == teamId);
                if (team is null) continue;

                var childTeamCount = allTeams.Count(t => t.ParentTeamId == teamId && t.IsActive);
                var summary = await _shiftMgmt.GetShiftsSummaryAsync(es.Id, teamId);

                departments.Add(new TeamsOverviewViewModel.DepartmentCard
                {
                    TeamId = teamId,
                    Name = teamName,
                    Slug = team.Slug,
                    Description = team.Description,
                    ChildTeamCount = childTeamCount,
                    TotalSlots = summary?.TotalSlots ?? 0,
                    FilledSlots = summary?.ConfirmedCount ?? 0
                });
            }

            var model = new TeamsOverviewViewModel { Departments = departments };
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Teams Overview");
            SetError("Failed to load teams.");
            return RedirectToAction(nameof(MyShifts));
        }
    }

    [HttpGet("Teams/{slug}")]
    public async Task<IActionResult> DepartmentDetail(string slug)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var team = await _teamService.GetTeamBySlugAsync(slug);
            if (team is null || team.ParentTeamId is not null) return NotFound();

            var isCoordinator = RoleChecks.IsAdmin(User) ||
                                User.IsInRole(RoleNames.VolunteerCoordinator) ||
                                await _shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);

            var allTeams = await _teamService.GetAllTeamsAsync();
            var childTeams = allTeams.Where(t => t.ParentTeamId == team.Id && t.IsActive).ToList();

            var pendingCounts = isCoordinator
                ? await _teamService.GetPendingRequestCountsByTeamIdsAsync(childTeams.Select(t => t.Id))
                : new Dictionary<Guid, int>();

            var childCards = new List<DepartmentDetailViewModel.ChildTeamCard>();
            foreach (var child in childTeams)
            {
                var members = await _teamService.GetTeamMembersAsync(child.Id);
                var summary = await _shiftMgmt.GetShiftsSummaryAsync(es.Id, child.Id);

                childCards.Add(new DepartmentDetailViewModel.ChildTeamCard
                {
                    TeamId = child.Id,
                    Name = child.Name,
                    Slug = child.Slug,
                    MemberCount = members.Count,
                    TotalSlots = summary?.TotalSlots ?? 0,
                    FilledSlots = summary?.ConfirmedCount ?? 0,
                    PendingRequestCount = pendingCounts.GetValueOrDefault(child.Id, 0)
                });
            }

            var model = new DepartmentDetailViewModel
            {
                Department = team,
                ChildTeams = childCards,
                IsCoordinator = isCoordinator,
                EventSettings = es
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading department detail for slug {Slug}", slug);
            SetError("Failed to load department.");
            return RedirectToAction(nameof(Teams));
        }
    }

    [HttpGet("Teams/{parentSlug}/{childSlug}")]
    public async Task<IActionResult> ChildTeamDetail(string parentSlug, string childSlug)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var parent = await _teamService.GetTeamBySlugAsync(parentSlug);
            if (parent is null || parent.ParentTeamId is not null) return NotFound();

            var child = await _teamService.GetTeamBySlugAsync(childSlug);
            if (child is null || child.ParentTeamId != parent.Id) return NotFound();

            var isCoordinator = RoleChecks.IsAdmin(User) ||
                                User.IsInRole(RoleNames.VolunteerCoordinator) ||
                                await _shiftMgmt.IsDeptCoordinatorAsync(user.Id, parent.Id);

            var members = await _teamService.GetTeamMembersAsync(child.Id);

            var rotas = await _shiftMgmt.GetRotasByDepartmentAsync(child.Id, es.Id);
            var rotaGroups = new List<RotaShiftGroup>();
            foreach (var rota in rotas)
            {
                var shifts = await _shiftMgmt.GetShiftsByRotaAsync(rota.Id);
                rotaGroups.Add(new RotaShiftGroup
                {
                    Rota = rota,
                    Shifts = shifts.Select(s =>
                    {
                        var (start, end, period) = _shiftMgmt.ResolveShiftTimes(s, es);
                        return new ShiftDisplayItem
                        {
                            Shift = s,
                            AbsoluteStart = start,
                            AbsoluteEnd = end,
                            Period = period,
                            ConfirmedCount = s.ShiftSignups.Count(su => su.Status == SignupStatus.Confirmed),
                            RemainingSlots = s.MaxVolunteers - s.ShiftSignups.Count(su => su.Status == SignupStatus.Confirmed)
                        };
                    }).OrderBy(s => s.AbsoluteStart).ToList()
                });
            }

            var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
            var userSignupShiftIds = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(s => s.ShiftId).ToHashSet();
            var userSignupStatuses = userSignups
                .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .ToDictionary(s => s.ShiftId, s => s.Status);

            var pendingRequests = isCoordinator
                ? (await _teamService.GetPendingRequestsForTeamAsync(child.Id)).ToList()
                : [];

            var model = new ChildTeamDetailViewModel
            {
                ChildTeam = child,
                Department = parent,
                Members = members.ToList(),
                Rotas = rotaGroups,
                PendingRequests = pendingRequests,
                IsCoordinator = isCoordinator,
                EventSettings = es,
                UserSignupShiftIds = userSignupShiftIds,
                UserSignupStatuses = userSignupStatuses
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading child team detail for {ParentSlug}/{ChildSlug}", parentSlug, childSlug);
            SetError("Failed to load team detail.");
            return RedirectToAction(nameof(Teams));
        }
    }

    [HttpPost("Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid signupId, string? returnUrl)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var signup = await _signupService.GetByIdAsync(signupId);
            if (signup is null)
            {
                SetError("Signup not found.");
                return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
            }

            var canApprove = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                             await _shiftMgmt.CanApproveSignupsAsync(user.Id, signup.Shift.Rota.TeamId);
            if (!canApprove) return Forbid();

            var result = await _signupService.ApproveAsync(signupId, user.Id);
            if (result.Success)
                SetSuccess("Signup approved.");
            else
                SetError(result.Error ?? "Approval failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving signup {SignupId}", signupId);
            SetError("Approval failed.");
        }
        return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
    }

    [HttpPost("Refuse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refuse(Guid signupId, string? reason, string? returnUrl)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var signup = await _signupService.GetByIdAsync(signupId);
            if (signup is null)
            {
                SetError("Signup not found.");
                return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
            }

            var canApprove = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                             await _shiftMgmt.CanApproveSignupsAsync(user.Id, signup.Shift.Rota.TeamId);
            if (!canApprove) return Forbid();

            var result = await _signupService.RefuseAsync(signupId, user.Id, reason);
            if (result.Success)
                SetSuccess("Signup refused.");
            else
                SetError(result.Error ?? "Refusal failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refusing signup {SignupId}", signupId);
            SetError("Refusal failed.");
        }
        return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
    }

    [HttpPost("NoShow")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NoShow(Guid signupId, string? returnUrl)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            var signup = await _signupService.GetByIdAsync(signupId);
            if (signup is null)
            {
                SetError("Signup not found.");
                return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
            }

            var canApprove = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                             await _shiftMgmt.CanApproveSignupsAsync(user.Id, signup.Shift.Rota.TeamId);
            if (!canApprove) return Forbid();

            var result = await _signupService.MarkNoShowAsync(signupId, user.Id);
            if (result.Success)
                SetSuccess("Marked as no-show.");
            else
                SetError(result.Error ?? "No-show marking failed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking no-show for signup {SignupId}", signupId);
            SetError("No-show marking failed.");
        }
        return RedirectToLocalOrAction(returnUrl, nameof(MyShifts));
    }

    [HttpPost("ApproveJoinRequest")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveJoinRequest(Guid requestId, string? returnUrl)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            await _teamService.ApproveJoinRequestAsync(requestId, user.Id, null);
            SetSuccess("Join request approved.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Invalid join request approval attempt for request {RequestId}", requestId);
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving join request {RequestId}", requestId);
            SetError("Failed to approve join request.");
        }
        return RedirectToLocalOrAction(returnUrl, nameof(Teams));
    }

    [HttpPost("RejectJoinRequest")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectJoinRequest(Guid requestId, string? reason, string? returnUrl)
    {
        try
        {
            var (err, user) = await ResolveCurrentUserOrChallengeAsync();
            if (err is not null) return err;

            await _teamService.RejectJoinRequestAsync(requestId, user.Id, reason ?? "Rejected by coordinator.");
            SetSuccess("Join request rejected.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Invalid join request rejection attempt for request {RequestId}", requestId);
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting join request {RequestId}", requestId);
            SetError("Failed to reject join request.");
        }
        return RedirectToLocalOrAction(returnUrl, nameof(Teams));
    }

    [HttpGet("Urgent")]
    public async Task<IActionResult> Urgent()
    {
        try
        {
            if (!ShiftRoleChecks.CanAccessDashboard(User))
                return Forbid();

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var urgentShifts = await _shiftMgmt.GetUrgentShiftsAsync(es.Id);

            var model = new UrgentShiftsViewModel
            {
                EventSettings = es,
                Shifts = urgentShifts.Select(u => new UrgentShiftsViewModel.UrgentShiftRow
                {
                    ShiftId = u.Shift.Id,
                    DutyTitle = u.Shift.Rota.Name + (string.IsNullOrEmpty(u.Shift.Description) ? "" : " — " + u.Shift.Description),
                    TeamName = u.DepartmentName,
                    TeamSlug = u.Shift.Rota.Team.Slug,
                    DayOffset = u.Shift.DayOffset,
                    StartTime = u.Shift.StartTime,
                    Duration = u.Shift.Duration,
                    Confirmed = u.ConfirmedCount,
                    MaxVolunteers = u.Shift.MaxVolunteers,
                    Priority = u.Shift.Rota.Priority,
                    UrgencyScore = u.UrgencyScore
                }).ToList()
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Urgent Shifts");
            SetError("Failed to load urgent shifts.");
            return RedirectToAction(nameof(MyShifts));
        }
    }

    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(Guid shiftId, Guid userId)
    {
        try
        {
            if (!ShiftRoleChecks.CanAccessDashboard(User))
                return Forbid();

            var (err, currentUser) = await ResolveCurrentUserOrUnauthorizedAsync();
            if (err is not null) return err;

            var result = await _signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
            if (result.Success)
                SetSuccess("Volunteer assigned to shift.");
            else
                SetError(result.Error ?? "Failed to assign volunteer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error voluntelling user {UserId} for shift {ShiftId}", userId, shiftId);
            SetError("Failed to assign volunteer.");
        }
        return RedirectToAction(nameof(Urgent));
    }

    [HttpGet("Management")]
    public async Task<IActionResult> Management()
    {
        try
        {
            if (!ShiftRoleChecks.CanAccessDashboard(User))
                return Forbid();

            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id);
            var confirmedCount = staffingData.Sum(d => d.ConfirmedCount);

            var model = new ManagementViewModel
            {
                SystemOpen = es.IsShiftBrowsingOpen,
                VolunteerCap = es.GlobalVolunteerCap,
                ConfirmedVolunteerCount = confirmedCount,
                EventSettings = es
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Management dashboard");
            SetError("Failed to load dashboard.");
            return RedirectToAction(nameof(MyShifts));
        }
    }

    [HttpGet("Settings")]
    [Authorize(Roles = RoleNames.Admin)]
    public async Task<IActionResult> Settings()
    {
        try
        {
            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id);
            var confirmedCount = staffingData.Sum(d => d.ConfirmedCount);

            var model = new SettingsViewModel
            {
                Id = es.Id,
                EventName = es.EventName,
                TimeZoneId = es.TimeZoneId,
                GateOpeningDate = LocalDatePattern.Iso.Format(es.GateOpeningDate),
                BuildStartOffset = es.BuildStartOffset,
                EventEndOffset = es.EventEndOffset,
                StrikeEndOffset = es.StrikeEndOffset,
                IsShiftBrowsingOpen = es.IsShiftBrowsingOpen,
                GlobalVolunteerCap = es.GlobalVolunteerCap,
                ConfirmedVolunteerCount = confirmedCount
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Vol Settings");
            SetError("Failed to load settings.");
            return RedirectToAction(nameof(Management));
        }
    }

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = RoleNames.Admin)]
    public async Task<IActionResult> Settings(bool isShiftBrowsingOpen, int? globalVolunteerCap)
    {
        try
        {
            var es = await _shiftMgmt.GetActiveAsync();
            if (es is null) return View("NoActiveEvent");

            es.IsShiftBrowsingOpen = isShiftBrowsingOpen;
            es.GlobalVolunteerCap = globalVolunteerCap;
            await _shiftMgmt.UpdateAsync(es);

            SetSuccess("Settings saved.");
            return RedirectToAction(nameof(Settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Vol Settings");
            SetError("Failed to save settings.");
            return RedirectToAction(nameof(Settings));
        }
    }

    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        if (!ShiftRoleChecks.CanAccessDashboard(User))
            return Forbid();

        if (!query.HasSearchTerm())
            return Json(Array.Empty<VolunteerSearchResult>());

        try
        {
            var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
            if (shift is null) return NotFound();

            var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
            if (es is null) return NotFound();

            var results = await ShiftVolunteerSearchBuilder.BuildAsync(
                shift, query, es,
                ShiftRoleChecks.CanViewMedical(User),
                UserManager, _profileService, _signupService, _availabilityService);
            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    private IActionResult RedirectToLocalOrAction(string? returnUrl, string actionName) =>
        Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl!)
            : RedirectToAction(actionName);
}

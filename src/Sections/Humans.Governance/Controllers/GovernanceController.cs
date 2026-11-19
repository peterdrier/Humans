using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Models.Tables;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Governance.Models;
using Humans.Users.Contracts;

namespace Humans.Governance.Controllers;

[Authorize]
[Route("[controller]")]
internal sealed class GovernanceController(
    IUserService userService,
    IGovernanceIndexService governanceIndexService,
    GovernanceBoardSeats boardSeats,
    IAuthorizationService authorization,
    IStringLocalizer<GovernanceResource> localizer) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var ct = HttpContext.RequestAborted;
        var data = await governanceIndexService.GetIndexDataAsync(user.Id, ct);
        var boardInfos = await UserService.GetUserInfosAsync(data.BoardMemberIds, ct);

        var viewModel = new GovernanceIndexViewModel
        {
            UserId = user.Id,
            StatutesContent = data.StatutesContent,
            HasApplication = data.HasApplication,
            ApplicationStatus = data.ApplicationStatus,
            ApplicationTier = data.ApplicationTier,
            ApplicationSubmittedAt = data.ApplicationSubmittedAt,
            ApplicationResolvedAt = data.ApplicationResolvedAt,
            ApplicationTermExpiresAt = data.ApplicationTermExpiresAt,
            ApplicationStatusBadgeClass = data.ApplicationStatus is { } status ? EnumBadgeMap.For(status) : "bg-secondary",
            CanApply = data.CanApply,
            IsApprovedColaborador = data.IsApprovedColaborador,
            ColaboradorCount = data.ColaboradorCount,
            AsociadoCount = data.AsociadoCount,
            BoardMemberIds = data.BoardMemberIds
                .OrderBy(id => boardInfos.TryGetValue(id, out var info) ? info.BurnerName : string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            OpenVotes = data.OpenVotes.OrderBy(v => v.ClosesAt).ToList(),
            UnvotedApplicationCount = data.UnvotedApplicationCount,
            YourAccess = [.. data.YourAccess.OrderBy(a => a.Body)]
        };

        return View(viewModel);
    }

    [HttpGet("{body:regex(^(Asociados|Colaboradores|Board)$)}")]
    public async Task<IActionResult> Body(string body)
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null || !Enum.TryParse<GovernanceBody>(body, ignoreCase: true, out var parsed))
            return NotFound();

        var data = await governanceIndexService.GetBodyPageAsync(parsed, ct);
        // A body's group and files are for its own members; Board and Admin see every body's.
        var seesLinks = data.MemberIds.Contains(user.Id)
            || (await authorization.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded;
        var infos = await UserService.GetUserInfosAsync(data.MemberIds, ct);

        return View(new GovernanceBodyViewModel
        {
            Body = data.Body,
            MemberIds = [.. data.MemberIds
                .OrderBy(id => infos.TryGetValue(id, out var info) ? info.BurnerName : string.Empty, StringComparer.CurrentCultureIgnoreCase)],
            Links = seesLinks ? data.Links : null,
            CanManageBoard = parsed == GovernanceBody.Board && await MayManageBoardAsync(),
        });
    }

    [HttpPost("Board/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBoardMember(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null || !await MayManageBoardAsync())
            return Forbid();
        if (await UserService.GetUserInfoAsync(userId, ct) is not { IsActive: true } member)
            return NotFound();

        var result = await boardSeats.AddAsync(member.Id, actor.Id, ct);
        if (result.Success)
            SetSuccess(localizer["Governance_BoardMemberAdded", member.BurnerName].Value);
        else
            SetError(localizer["Governance_BoardMemberAlreadyOn", member.BurnerName].Value);
        return RedirectToAction(nameof(Body), new { body = nameof(GovernanceBody.Board) });
    }

    [HttpPost("Board/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndBoardMember(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null || !await MayManageBoardAsync())
            return Forbid();

        var result = await boardSeats.EndAsync(userId, actor.Id, ct);
        var name = (await UserService.GetUserInfoAsync(userId, ct))?.BurnerName ?? string.Empty;
        if (result.Success)
            SetSuccess(localizer["Governance_BoardMemberEnded", name].Value);
        else
            SetError(localizer["Governance_BoardMemberNotOn", name].Value);
        return RedirectToAction(nameof(Body), new { body = nameof(GovernanceBody.Board) });
    }

    private async Task<bool> MayManageBoardAsync() =>
        (await authorization.AuthorizeAsync(User, RoleNames.Board, PolicyNames.RoleAssignmentManage)).Succeeded;
}

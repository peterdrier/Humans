using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Models.Tables;
using Humans.Google.Contracts;
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
    GovernanceBodyLinksService bodyLinks,
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
        var positions = await boardSeats.GetPositionsAsync(ct);

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
            CurrentTier = data.CurrentTier,
            IsBoardMember = data.BoardMemberIds.Contains(user.Id),
            MemberSince = data.MemberSince?.ToDateTimeUtc(),
            ColaboradorCount = data.ColaboradorCount,
            AsociadoCount = data.AsociadoCount,
            BoardMemberIds = OrderByOffice(data.BoardMemberIds, positions, boardInfos),
            BoardPositions = positions,
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
        // A body's group and files are for its own members; Board and Admin see every body's, and manage them.
        var mayManageLinks = await MayManageLinksAsync();
        var seesLinks = mayManageLinks || data.MemberIds.Contains(user.Id);
        var infos = await UserService.GetUserInfosAsync(data.MemberIds, ct);
        var positions = parsed == GovernanceBody.Board
            ? await boardSeats.GetPositionsAsync(ct)
            : new Dictionary<Guid, BoardPosition>();

        return View(new GovernanceBodyViewModel
        {
            Body = data.Body,
            MemberIds = OrderByOffice(data.MemberIds, positions, infos),
            Positions = positions,
            Links = seesLinks ? data.Links : null,
            CanManageBoard = parsed == GovernanceBody.Board && await MayManageBoardAsync(),
            Manage = mayManageLinks ? await GetManageAsync(parsed, ct) : null,
        });
    }

    [HttpPost("{body:regex(^(Asociados|Colaboradores|Board)$)}/Group")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetGroup(string body, string? prefix) =>
        ManageLinksAsync(body, (parsed, actorId, ct) => bodyLinks.SetGroupAsync(parsed, prefix, actorId, ct), "Governance_Manage_GroupSaved");

    [HttpPost("{body:regex(^(Asociados|Colaboradores|Board)$)}/Files/Add")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddFile(string body, string url, DrivePermissionLevel level) =>
        ManageLinksAsync(body, (parsed, actorId, ct) => bodyLinks.AddFileAsync(parsed, url ?? string.Empty, level, actorId, ct),
            "Governance_Manage_FileAdded");

    [HttpPost("{body:regex(^(Asociados|Colaboradores|Board)$)}/Files/{fileId:guid}/Remove")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveFile(string body, Guid fileId) =>
        ManageLinksAsync(body, (parsed, actorId, ct) => bodyLinks.RemoveFileAsync(parsed, fileId, actorId, ct), "Governance_Manage_FileRemoved");

    private async Task<IActionResult> ManageLinksAsync(
        string body, Func<GovernanceBody, Guid, CancellationToken, Task<GovernanceBodyLinkResult>> change, string successKey)
    {
        var ct = HttpContext.RequestAborted;
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null || !await MayManageLinksAsync())
            return Forbid();
        if (!Enum.TryParse<GovernanceBody>(body, ignoreCase: true, out var parsed))
            return NotFound();

        var result = await change(parsed, actor.Id, ct);
        if (result.Success)
            SetSuccess(localizer[successKey].Value);
        else
            SetError(result.GoogleMessage ?? localizer[result.ErrorKey!].Value);
        return RedirectToAction(nameof(Body), new { body = parsed.ToString() });
    }

    private async Task<GovernanceBodyManageModel> GetManageAsync(GovernanceBody body, CancellationToken ct)
    {
        var stored = (await bodyLinks.GetAllAsync(ct)).SingleOrDefault(l => l.Body == body);
        return new GovernanceBodyManageModel(
            stored?.GoogleGroupPrefix,
            [.. (stored?.Files ?? []).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new GovernanceBodyManageFile(f.Id, f.Name, f.Url, f.PermissionLevel))]);
    }

    private async Task<bool> MayManageLinksAsync() =>
        (await authorization.AuthorizeAsync(User, PolicyNames.BoardOrAdmin)).Succeeded;

    [HttpPost("Board/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBoardMember(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null || !await MayManageBoardAsync())
            return Forbid();
        var result = await boardSeats.AddAsync(userId, actor.Id, ct);
        if (string.Equals(result.ErrorKey, GovernanceBoardSeats.InactiveAccount, StringComparison.Ordinal))
            return NotFound();

        var member = await UserService.GetUserInfoAsync(userId, ct);
        if (result.Success)
            SetSuccess(localizer["Governance_BoardMemberAdded", member?.BurnerName ?? string.Empty].Value);
        else
            SetError(localizer["Governance_BoardMemberAlreadyOn", member?.BurnerName ?? string.Empty].Value);
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

    /// <summary>Gives a Board member an office, or with no office clears the one they hold.</summary>
    [HttpPost("Board/Position")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetBoardPosition(Guid userId, BoardPosition? position)
    {
        var ct = HttpContext.RequestAborted;
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null || !await MayManageBoardAsync())
            return Forbid();

        var result = position is { } office
            ? await boardSeats.SetPositionAsync(office, userId, actor.Id, ct)
            : await boardSeats.ClearPositionAsync(userId, actor.Id, ct);
        if (result.Success)
            SetSuccess(localizer["Governance_BoardPositionSaved"].Value);
        else
            SetError(localizer["Governance_BoardPositionNotOnBoard"].Value);
        return RedirectToAction(nameof(Body), new { body = nameof(GovernanceBody.Board) });
    }

    private static List<Guid> OrderByOffice(
        IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, BoardPosition> positions, IReadOnlyDictionary<Guid, UserInfo> infos) =>
        [.. ids
            .OrderBy(id => positions.TryGetValue(id, out var p) ? (int)p : int.MaxValue)
            .ThenBy(id => infos.TryGetValue(id, out var info) ? info.BurnerName : string.Empty, StringComparer.CurrentCultureIgnoreCase)];

    private async Task<bool> MayManageBoardAsync() =>
        (await authorization.AuthorizeAsync(User, RoleNames.Board, PolicyNames.RoleAssignmentManage)).Succeeded;
}

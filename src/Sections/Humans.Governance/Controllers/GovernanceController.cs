using Humans.Base.Authorization;
using Humans.Base.Models.Tables;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Governance.Models;
using Humans.Users.Contracts;

namespace Humans.Governance.Controllers;

[Authorize]
[Route("[controller]")]
internal sealed class GovernanceController(
    IUserService userService,
    IGovernanceIndexService governanceIndexService) : HumansControllerBase(userService)
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
            YourAccess = data.YourAccess
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

        var data = await governanceIndexService.GetBodyPageAsync(parsed, user.Id, RoleChecks.IsAdminOrBoard(User), ct);
        var infos = await UserService.GetUserInfosAsync(data.MemberIds, ct);

        return View(new GovernanceBodyViewModel
        {
            Body = data.Body,
            MemberIds = [.. data.MemberIds
                .OrderBy(id => infos.TryGetValue(id, out var info) ? info.BurnerName : string.Empty, StringComparer.CurrentCultureIgnoreCase)],
            Links = data.Links,
        });
    }
}

using Humans.Base.Models.Tables;
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
    IUserServiceRead userService,
    IGovernanceIndexService governanceIndexService) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var data = await governanceIndexService.GetIndexDataAsync(user.Id);

        var viewModel = new GovernanceIndexViewModel
        {
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
            AsociadoCount = data.AsociadoCount
        };

        return View(viewModel);
    }
}

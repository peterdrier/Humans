using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Governance;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController(
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
            ApplicationStatusBadgeClass = data.ApplicationStatus.GetBadgeClass(),
            CanApply = data.CanApply,
            IsApprovedColaborador = data.IsApprovedColaborador,
            ColaboradorCount = data.ColaboradorCount,
            AsociadoCount = data.AsociadoCount
        };

        return View(viewModel);
    }
}

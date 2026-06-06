using Humans.Application.Interfaces.Admin;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Users/Admin")]
public sealed class UsersAdminController(
    IUserServiceRead userService,
    IAdminDatabaseDiagnosticsService databaseDiagnostics,
    ILogger<UsersAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Audience")]
    public async Task<IActionResult> Audience(int? year, CancellationToken ct)
    {
        try
        {
            var segmentation = await databaseDiagnostics.GetAudienceSegmentationAsync(year, ct);

            var model = new AudienceSegmentationViewModel
            {
                TotalAccounts = segmentation.TotalAccounts,
                WithTicket = segmentation.WithTicket,
                WithProfile = segmentation.WithProfile,
                WithBoth = segmentation.WithBoth,
                WithNeither = segmentation.WithNeither,
                AvailableYears = segmentation.AvailableYears.ToList(),
                SelectedYear = segmentation.SelectedYear,
            };

            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading audience segmentation data");
            SetError("Failed to load audience segmentation data.");
            return RedirectToAction(nameof(AdminController.Index), "Admin");
        }
    }
}

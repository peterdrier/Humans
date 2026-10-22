using Humans.Base.Configuration;
using Humans.Base.Controllers;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Humans.Consent.Controllers;

[Authorize]
internal sealed class MyDataController(
    IUserService userService,
    IConsentManagement consentService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry) : HumansControllerBase(userService)
{
    [HttpGet("Consent/MyData")]
    public async Task<IActionResult> Index()
    {
        var ct = HttpContext.RequestAborted;
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        var (_, history) = await consentService.GetConsentDashboardAsync(user.Id, ct);

        var viewModel = new MyDataViewModel
        {
            IsDeletionPending = user.IsDeletionPending,
            DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc(),
            DeletionScheduledFor = user.EffectiveDeletionDate?.ToDateTimeUtc(),
            SignedDocuments = history
        };

        ViewData["DpoEmail"] = configuration.GetOptionalSetting(configRegistry, "Email:DpoAddress", "Email", importance: ConfigurationImportance.Recommended);
        return View(viewModel);
    }
}

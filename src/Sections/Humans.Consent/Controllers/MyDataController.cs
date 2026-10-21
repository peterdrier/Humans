using Humans.Base.Configuration;
using Humans.Base.Controllers;
using Humans.Consent.Models;
using Humans.Consent.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Humans.Consent.Controllers;

/// <summary>
/// The member's My Data page at <c>/Profile/Me/Privacy</c> (URL kept, as Shifts'
/// <c>ShiftProfileController</c> keeps the <c>Profile/Me</c> prefix). It composes the pending-deletion
/// status from <c>UserInfo</c> and the legal documents the member signed; the export and deletion
/// actions it links to are Gdpr's <c>MemberDataController</c> and <c>UserController</c>.
/// </summary>
[Authorize]
internal sealed class MyDataController(
    IUserService userService,
    IConsentManagement consentService,
    IConfiguration configuration,
    ConfigurationRegistry configRegistry) : HumansControllerBase(userService)
{
    [HttpGet("Profile/Me/Privacy")]
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

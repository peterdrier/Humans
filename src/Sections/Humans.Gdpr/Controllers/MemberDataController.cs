using System.Globalization;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

/// <summary>
/// A member's own subject-rights actions from the My Data page: the Article 15 data export and
/// the Article 17 deletion request. Both act on the signed-in member only and never take a
/// user id. The page itself (<c>/Profile/Me/Privacy</c>) is Consent's (<c>MyDataController</c>) and links here; the
/// routes keep the <c>Profile/Me</c> prefix so member URLs do not change, as Shifts'
/// <c>ShiftProfileController</c> does. The landing pages for the pending deletion are
/// <see cref="UserController"/>.
/// </summary>
[Authorize]
internal sealed class MemberDataController(
    IUserService userService,
    IGdprService gdprExportService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<MemberDataController> logger,
    IStringLocalizer<GdprResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    [HttpPost("Profile/Me/Privacy/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await accountDeletionService.RequestDeletionAsync(user.Id);
        if (!result.Success)
        {
            if (string.Equals(result.ErrorKey, "AlreadyPending", StringComparison.Ordinal))
                SetError(localizer["Gdpr_Deletion_AlreadyPending"].Value);
            return RedirectToAction("Index", "MyData");
        }

        SetSuccess(string.Format(CultureInfo.CurrentCulture,
            localizer["Gdpr_Deletion_Requested"].Value,
            result.EffectiveDeletionDate?.ToDateTimeUtc().ToDate() ?? ""));
        return RedirectToAction("Index", "MyData");
    }

    [HttpGet("Profile/Me/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        try
        {
            var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

            var bytes = GdprExportSerializer.Serialize(export);
            var fileName = $"nobodies-profiles-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError(sharedLocalizer["Error_TryAgainLater"].Value);
            return RedirectToAction("Index", "MyData");
        }
    }
}

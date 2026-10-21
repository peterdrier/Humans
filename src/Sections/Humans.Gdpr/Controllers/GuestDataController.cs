using System.Globalization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Base;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Gdpr.Controllers;

/// <summary>
/// The subject-rights actions for profileless accounts (authenticated users without a Profile):
/// the Article 15 data export and the Article 17 deletion request and its cancellation. The
/// deletion actions moved here from Users' <c>GuestAccountController</c> in Users decomposition
/// commit 14 and take Gdpr's <c>/Gdpr/Guest/*</c> URLs, beside their member counterparts on
/// <see cref="MemberDataController"/>. The export moved with them to <c>/Gdpr/Guest/DownloadData</c>.
/// </summary>
[Authorize]
internal sealed class GuestDataController(
    IUserService userService,
    IGdprService gdprExportService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<GuestDataController> logger,
    IStringLocalizer<SharedResource> localizer,
    IStringLocalizer<GdprResource> gdprLocalizer) : HumansControllerBase(userService)
{
    [HttpGet("Gdpr/Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var (noCurrentUser, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (noCurrentUser is not null)
            return noCurrentUser;

        try
        {
            var export = await gdprExportService.ExportForUserAsync(user.Id, ct);

            var bytes = GdprExportSerializer.Serialize(export);
            var fileName = $"nobodies-data-export-{clock.GetCurrentInstant().ToDateTimeUtc().ToInvariantDate()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError(localizer["Error_TryAgainLater"].Value);
            return RedirectToAction("Index", "Guest");
        }
    }

    [HttpPost("Gdpr/Guest/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        try
        {
            // Single orchestrator for profile + profileless deletion (see #685).
            var result = await accountDeletionService.RequestDeletionAsync(user.Id);
            var flash = GuestDeletionRequestFlash.From(result);
            if (flash.Success)
                SetSuccess(FormatDeletionFlash(flash));
            else
                SetError(FormatDeletionFlash(flash));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process deletion request for user {UserId}", user.Id);
            SetError(gdprLocalizer["Gdpr_Guest_DeletionRequestFailed"].Value);
        }

        return RedirectToAction("Index", "Guest");
    }

    [HttpPost("Gdpr/Guest/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        var result = await accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            SetError(string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal)
                ? gdprLocalizer["Gdpr_Deletion_NotPending"].Value
                : gdprLocalizer["Gdpr_Guest_CancelDeletionFailed"].Value);
            return RedirectToAction("Index", "Guest");
        }

        SetSuccess(gdprLocalizer["Gdpr_Deletion_Cancelled"].Value);
        return RedirectToAction("Index", "Guest");
    }

    private string FormatDeletionFlash(GuestDeletionRequestFlash flash) =>
        string.Format(CultureInfo.CurrentCulture, gdprLocalizer[flash.ResourceKey].Value, flash.EffectiveDeletionDate.ToDate());
}

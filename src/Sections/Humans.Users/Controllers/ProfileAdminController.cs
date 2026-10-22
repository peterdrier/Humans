using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Users.Models.EmailProblems;
using Humans.Users.Services;

namespace Humans.Users.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin")]
internal sealed class ProfileAdminController(
    IUserService userService,
    IEmailProblemsService emailProblems,
    IUserEmailService userEmails,
    IUserManagement users,
    IAuditLogService audit) : HumansControllerBase(userService)
{
    [HttpGet("EmailProblems")]
    public async Task<IActionResult> EmailProblems(CancellationToken ct)
    {
        var report = await emailProblems.ScanAsync(ct);

        // Raw: the report names the exact rows holding each address, tombstones included, and the page deletes by them.
        var allInvolvedUserIds = report.Problems
            .SelectMany(p => new[] { p.UserId, p.OtherUserId })
            .OfType<Guid>()
            .ToHashSet();
        var users1 = (await users.GetAllRawUserInfosAsync(ct))
            .Where(u => allInvolvedUserIds.Contains(u.Id))
            .ToDictionary(u => u.Id);

        return View(EmailProblemsListViewModel.From(report, users1));
    }

    [HttpPost("EmailProblems/DeleteOrphanEmail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrphanEmail(Guid emailId, CancellationToken ct)
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        var deleted = await userEmails.DeleteByIdAsync(emailId, ct);
        if (deleted)
        {
            await audit.LogAsync(
                AuditAction.OrphanUserEmailDeleted, nameof(UserEmail), emailId,
                $"Orphan UserEmail row {emailId} deleted by EmailProblems action",
                currentUser.Id);
            SetSuccess("Orphan email row deleted.");
        }
        else
        {
            SetInfo("Already cleaned up — no row to delete.");
        }
        return RedirectToAction(nameof(EmailProblems));
    }
}

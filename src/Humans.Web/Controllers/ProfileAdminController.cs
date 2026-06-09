using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models.EmailProblems;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Profile/Admin")]
public class ProfileAdminController(
    IUserServiceRead userService,
    IEmailProblemsService emailProblems,
    IUserEmailService userEmails,
    IUserService users,
    IAuditLogService audit) : HumansControllerBase(userService)
{
    [HttpGet("EmailProblems")]
    public async Task<IActionResult> EmailProblems(CancellationToken ct)
    {
        var report = await emailProblems.ScanAsync(ct);

        var allInvolvedUserIds = report.Problems
            .SelectMany(p => new[] { p.UserId, p.OtherUserId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var users1 = await users.GetUserInfosAsync(allInvolvedUserIds, ct);

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

    [HttpPost("EmailProblems/BackfillLegacyEmails")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BackfillLegacyEmails(CancellationToken ct)
    {
        var (error, currentUser) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        var backfilled = await emailProblems.BackfillLegacyIdentityEmailsAsync(currentUser.Id, ct);
        foreach (var (userId, email) in backfilled)
        {
            await audit.LogAsync(
                AuditAction.LegacyIdentityEmailBackfilled, nameof(User), userId,
                $"Backfilled verified UserEmail row from legacy User.Email column: {email}",
                currentUser.Id);
        }

        if (backfilled.Count == 0)
            SetInfo("No legacy User.Email values to backfill.");
        else
            SetSuccess($"Backfilled {backfilled.Count} verified UserEmail row(s) from legacy User.Email columns.");

        return RedirectToAction(nameof(EmailProblems));
    }
}

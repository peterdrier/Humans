using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Workgroups.Controllers;

/// <summary>
/// The Secretary's and the Board's screens. Routed under <c>/Workgroups/Admin</c> rather
/// than <c>/Admin</c>: admin pages belong to their section's URL space
/// (memory/architecture/no-admin-url-section.md). Localization-exempt, per §19.
/// </summary>
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Workgroups/Admin")]
internal sealed class WorkgroupsAdminController(
    IWorkgroupService workgroups,
    IUserServiceRead users,
    IStringLocalizer<WorkgroupsResource> localizer,
    IClock clock,
    ILogger<WorkgroupsAdminController> logger) : HumansControllerBase(users)
{
    // ── The queue ─────────────────────────────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var register = await workgroups.GetRegisterAsync(ct);
        return View(new AdminQueueViewModel
        {
            Now = clock.GetCurrentInstant(),
            Register = register,
            People = await PeopleAsync(register, ct),
            RootDriveFolderId = await workgroups.GetRootDriveFolderIdAsync(ct)
        });
    }

    // ── The decisions of §6 ───────────────────────────────────────────────

    [HttpPost("{id:guid}/Register")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Register(Guid id, CancellationToken ct) =>
        ActAsync(actor => workgroups.RegisterAsync(id, actor, ct), "Registered", ct);

    [HttpPost("{id:guid}/Refer")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Refer(Guid id, string? note, CancellationToken ct) =>
        ActAsync(actor => workgroups.ReferAsync(id, actor, note, ct), "Referred to the Board", ct);

    [HttpPost("{id:guid}/Refuse")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Refuse(Guid id, string reasons, CancellationToken ct) =>
        ActAsync(actor => workgroups.RefuseAsync(id, actor, reasons, ct), "Registration refused", ct);

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Withdraw(Guid id, string reasons, CancellationToken ct) =>
        ActAsync(actor => workgroups.WithdrawAsync(id, actor, reasons, ct), "Registration withdrawn", ct);

    [HttpPost("{id:guid}/Close")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Close(Guid id, string reasons, CancellationToken ct) =>
        ActAsync(actor => workgroups.CloseAsync(id, actor, reasons, ct), "Group closed", ct);

    [HttpPost("{id:guid}/Reactivate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Reactivate(Guid id, CancellationToken ct) =>
        ActAsync(actor => workgroups.ReactivateAsync(id, actor, ct), "Group reactivated", ct);

    /// <summary>The Board's override of §5's member-run handover — a coordinatorless group needs one.</summary>
    [HttpPost("{id:guid}/Coordinators")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Coordinators(Guid id, Guid[] coordinatorUserIds, CancellationToken ct) =>
        ActAsync(actor => workgroups.SetCoordinatorsAsync(id, actor, coordinatorUserIds, asAdmin: true, ct),
            "Coordinators set", ct);

    // ── Bootstrapping (§21) ───────────────────────────────────────────────

    [HttpGet("RegisterExisting")]
    public IActionResult RegisterExisting() =>
        View(new RegisterExistingViewModel { RegisteredOn = clock.GetCurrentInstant().InUtc().Date });

    [HttpPost("RegisterExisting")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterExisting(RegisterExistingViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (!ModelState.IsValid) return View(model);

        try
        {
            // Backdated to when the group really started; midnight UTC is precise enough for
            // a date somebody typed from memory.
            var registeredAt = model.RegisteredOn.AtMidnight().InUtc().ToInstant();
            await workgroups.RegisterExistingAsync(user.Id,
                new WorkgroupBootstrap(model.Application.ToApplication(), model.CoordinatorUserId, registeredAt),
                ct);
        }
        catch (WorkgroupRuleException ex)
        {
            logger.LogInformation(ex, "Workgroups admin RegisterExisting: rule {Rule}", ex.Key);
            ModelState.AddModelError(string.Empty, localizer[ex.Key, ex.Args]);
            return View(model);
        }

        SetSuccess("Existing group registered");
        return RedirectToAction(nameof(Index));
    }

    // ── The Board's reply ─────────────────────────────────────────────────

    [HttpPost("Documents/{id:guid}/Disposition")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Disposition(
        Guid id, WorkgroupDisposition disposition, string note, CancellationToken ct) =>
        ActAsync(actor => workgroups.RecordDispositionAsync(id, actor, disposition, note, ct),
            "Disposition recorded", ct);

    // ── Settings ──────────────────────────────────────────────────────────

    [HttpGet("Settings")]
    public async Task<IActionResult> Settings(CancellationToken ct) =>
        View(new WorkgroupsSettingsViewModel
        {
            RootDriveFolderId = await workgroups.GetRootDriveFolderIdAsync(ct) ?? string.Empty
        });

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(WorkgroupsSettingsViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (!ModelState.IsValid) return View(model);

        try
        {
            await workgroups.SetRootDriveFolderIdAsync(model.RootDriveFolderId, user.Id, ct);
        }
        catch (WorkgroupRuleException ex)
        {
            ModelState.AddModelError(nameof(model.RootDriveFolderId), localizer[ex.Key, ex.Args]);
            return View(model);
        }

        SetSuccess("Root Drive folder saved");
        return RedirectToAction(nameof(Index));
    }

    // ── Plumbing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Admin POSTs all land back on the queue. Messages are plain English on purpose: these
    /// pages are localization-exempt (§19).
    /// </summary>
    private async Task<IActionResult> ActAsync(Func<Guid, Task> action, string success, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        try
        {
            await action(user.Id);
            SetSuccess(success);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Workgroups admin {Action}: not found",
                ControllerContext.ActionDescriptor.ActionName);
            return NotFound();
        }
        catch (WorkgroupRuleException ex)
        {
            logger.LogInformation(ex, "Workgroups admin {Action}: rule {Rule}",
                ControllerContext.ActionDescriptor.ActionName, ex.Key);
            SetError(localizer[ex.Key, ex.Args]);
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> PeopleAsync(
        IReadOnlyList<WorkgroupInfo> register, CancellationToken ct)
    {
        var ids = register
            .SelectMany(w => w.Members.Select(m => m.UserId))
            .Distinct()
            .ToList();

        return ids.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(ids, ct);
    }
}

using Humans.Base.Controllers;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Authorization;
using Humans.Preapprovals.Models;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Preapprovals.Controllers;

/// <summary>
/// The managers' and the Board's screens. Routed under <c>/Preapprovals/Admin</c> rather
/// than <c>/Admin</c>: admin pages belong to their section's URL space
/// (memory/architecture/no-admin-url-section.md). Localization-exempt. The policy admits any
/// program manager; per-program operations are checked against the program resource.
/// </summary>
[Authorize(Policy = PreapprovalsPolicies.Manage)]
[Route("Preapprovals/Admin")]
internal sealed class PreapprovalsAdminController(
    IPreapprovalManagement preapprovals,
    IUserService users,
    IHoldedFinanceService finance,
    IStringLocalizer<PreapprovalsResource> localizer,
    IAuthorizationService authorization,
    IClock clock,
    ILogger<PreapprovalsAdminController> logger) : HumansControllerBase(users)
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    // ── Programs ──────────────────────────────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var canAdminister = PreapprovalsPolicyHandler.IsManagingRole(User);
        var programs = await preapprovals.GetProgramsAsync(ct);
        if (!canAdminister)
            programs = programs.Where(p => p.IsManager(user.Id)).ToList();
        return View(new AdminIndexViewModel
        {
            Today = Today,
            Programs = programs.OrderBy(p => p.Status).ThenBy(p => p.ClaimBy).ToList(),
            CanAdminister = canAdminister
        });
    }

    [HttpGet("New")]
    public async Task<IActionResult> New(CancellationToken ct)
    {
        if (!PreapprovalsPolicyHandler.IsManagingRole(User)) return Forbid();
        return View("Form", await FillAsync(new ProgramFormViewModel { ClaimBy = Today.PlusMonths(6) }, ct));
    }

    [HttpPost("New")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(ProgramFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (!PreapprovalsPolicyHandler.IsManagingRole(User)) return Forbid();
        if (!ModelState.IsValid) return View("Form", await FillAsync(model, ct));

        var result = await preapprovals.CreateProgramAsync(model.ToSave(), user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Preapprovals admin New: rule {Rule}", refusal.Key);
            ModelState.AddModelError(string.Empty, localizer[refusal.Key, refusal.Args]);
            return View("Form", await FillAsync(model, ct));
        }
        SetSuccess("Program created");
        return RedirectToAction(nameof(Program), new { programId = result.Value });
    }

    [HttpGet("{programId:guid}")]
    public async Task<IActionResult> Program(Guid programId, CancellationToken ct)
    {
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Manage, ct);
        if (forbidden is not null) return forbidden;

        var ids = program!.Grants.Select(g => g.BeneficiaryUserId)
            .Concat(program.Grants.Select(g => g.DecidedByUserId).OfType<Guid>())
            .Concat(program.ManagerUserIds).Distinct().ToList();
        return View(new AdminProgramViewModel
        {
            Program = program,
            Today = Today,
            CanAdminister = PreapprovalsPolicyHandler.IsManagingRole(User),
            People = await UserService.GetUserInfosAsync(ids, ct)
        });
    }

    [HttpGet("{programId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid programId, CancellationToken ct)
    {
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Administer, ct);
        if (forbidden is not null) return forbidden;
        return View("Form", await FillAsync(ProgramFormViewModel.From(program!), ct));
    }

    [HttpPost("{programId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid programId, ProgramFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Administer, ct);
        if (forbidden is not null) return forbidden;
        model.Id = programId;
        model.KindLocked = program!.KindLocked;
        if (!ModelState.IsValid) return View("Form", await FillAsync(model, ct));

        var result = await preapprovals.UpdateProgramAsync(programId, model.ToSave(), user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Preapprovals admin Edit: rule {Rule}", refusal.Key);
            ModelState.AddModelError(string.Empty, localizer[refusal.Key, refusal.Args]);
            return View("Form", await FillAsync(model, ct));
        }
        SetSuccess("Program saved");
        return RedirectToAction(nameof(Program), new { programId });
    }

    // ── Grants ────────────────────────────────────────────────────────────

    [HttpPost("{programId:guid}/Issue")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Issue(Guid programId, Guid? userId, decimal? amount, string? label, CancellationToken ct) =>
        ManageAsync(programId, actor => userId is { } id
                ? preapprovals.IssueAsync(programId, id, amount, label, actor, ct)
                : Task.FromResult(new PreapprovalMutationResult<Guid>(Refusal: new PreapprovalRefusal(PreapprovalErrorKeys.NoBeneficiaries))),
            "Pre-approval issued", ct);

    [HttpPost("{programId:guid}/IssueMany")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> IssueMany(Guid programId, Guid[] userIds, CancellationToken ct) =>
        ManageAsync(programId, actor => preapprovals.IssueManyAsync(programId, userIds, actor, ct),
            $"{userIds.Distinct().Count()} pre-approvals issued", ct);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Approve")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(Guid programId, Guid grantId, decimal? amount, CancellationToken ct) =>
        ManageAsync(programId, actor => preapprovals.ApproveAsync(grantId, amount, actor, ct), "Application approved", ct, grantId);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Decline")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Decline(Guid programId, Guid grantId, string? note, CancellationToken ct) =>
        ManageAsync(programId, actor => preapprovals.DeclineAsync(grantId, note, actor, ct), "Application declined", ct, grantId);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Revoke")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Revoke(Guid programId, Guid grantId, string? reason, CancellationToken ct) =>
        ManageAsync(programId, actor => preapprovals.RevokeAsync(grantId, reason, actor, ct), "Pre-approval revoked", ct, grantId);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Amount")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Amount(Guid programId, Guid grantId, decimal amount, CancellationToken ct) =>
        ManageAsync(programId, actor => preapprovals.ChangeAmountAsync(grantId, amount, actor, ct), "Amount changed", ct, grantId);

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Loads the program and checks the operation against it; 404 and 403 as results.</summary>
    private async Task<(ProgramInfo? Program, IActionResult? Error)> AuthorizedAsync(
        Guid programId, ProgramOperationRequirement requirement, CancellationToken ct)
    {
        var program = await preapprovals.GetProgramAsync(programId, ct);
        if (program is null) return (null, NotFound());
        var allowed = await authorization.AuthorizeAsync(User, program, requirement);
        return allowed.Succeeded ? (program, null) : (null, Forbid());
    }

    /// <summary>
    /// An action on a program the actor manages. A grant route names the program it is under;
    /// a grant of another program is 404 so a manager of A never acts on B's grants.
    /// </summary>
    private async Task<IActionResult> ManageAsync(
        Guid programId, Func<Guid, Task<PreapprovalMutationResult>> act, string success, CancellationToken ct,
        Guid? grantId = null)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Manage, ct);
        if (forbidden is not null) return forbidden;
        if (grantId is { } id && program!.Grants.All(g => g.Id != id)) return NotFound();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed grant action on {ProgramId} by {UserId}", programId, user.Id);
            return BadRequest(ModelState);
        }

        var result = await act(user.Id);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Preapprovals admin action on {ProgramId}: rule {Rule}", programId, refusal.Key);
            SetError(localizer[refusal.Key, refusal.Args]);
        }
        else
        {
            SetSuccess(success);
        }
        return RedirectToAction(nameof(Program), new { programId });
    }

    private async Task<IActionResult> ManageAsync<T>(
        Guid programId, Func<Guid, Task<PreapprovalMutationResult<T>>> act, string success, CancellationToken ct) =>
        await ManageAsync(programId, async actor => (PreapprovalMutationResult)await act(actor), success, ct);

    private async Task<ProgramFormViewModel> FillAsync(ProgramFormViewModel model, CancellationToken ct)
    {
        model.Accounts = await finance.ListExpenseAccountsAsync(activeOnly: true, ct);
        model.People = await UserService.GetUserInfosAsync(model.ManagerUserIds, ct);
        return model;
    }
}

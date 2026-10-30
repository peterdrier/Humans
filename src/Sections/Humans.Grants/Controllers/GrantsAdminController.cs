using Humans.Base.Controllers;
using Humans.Grants.Authorization;
using Humans.Grants.Contracts;
using Humans.Grants.Models;
using Humans.Grants.Services;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Grants.Controllers;

/// <summary>
/// The class policy admits any program manager and Board; each action names its own policy too, and per-program
/// operations are checked against the program resource, which denies Board every change.
/// </summary>
[Authorize(Policy = GrantProgramsPolicy.Name)]
[Route("Grants/Admin")]
internal sealed class GrantsAdminController(
    IGrantManagement grants,
    IUserService users,
    IAuthorizationService authorization,
    ISettingsService settings,
    ILogger<GrantsAdminController> logger) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var programs = await grants.GetProgramsAsync(ct);
        if (!(await authorization.AuthorizeAsync(User, GrantsPolicies.Read)).Succeeded)
            programs = programs.Where(p => p.IsManager(user.Id)).ToList();
        return View(new AdminIndexViewModel
        {
            Today = grants.Today,
            Programs = programs.OrderBy(p => p.Status).ThenByDescending(p => p.Year).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            CanAdminister = (await authorization.AuthorizeAsync(User, GrantsPolicies.Manage)).Succeeded
        });
    }

    [HttpGet("New")]
    [Authorize(Policy = GrantsPolicies.Manage)]
    public async Task<IActionResult> New(CancellationToken ct)
    {
        var active = await settings.GetActiveEventSettingsAsync(ct);
        return View("Form", await FillAsync(new ProgramFormViewModel { EventSettingsId = active?.Id }, ct));
    }

    [HttpPost("New")]
    [Authorize(Policy = GrantsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> New(ProgramFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (!ModelState.IsValid) return View("Form", await FillAsync(model, ct));

        var result = await grants.CreateProgramAsync(model.ToSave(), user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            ModelState.AddModelError(string.Empty, refusal);
            return View("Form", await FillAsync(model, ct));
        }
        SetSuccess("Program created");
        return RedirectToAction(nameof(Program), new { programId = result.Value });
    }

    [HttpGet("{programId:guid}")]
    public async Task<IActionResult> Program(Guid programId, CancellationToken ct)
    {
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.View, ct);
        if (forbidden is not null) return forbidden;

        var ids = program!.Grants.Select(g => g.IssuedByUserId)
            .Concat(program.Grants.Select(g => g.RevokedByUserId).OfType<Guid>())
            .Concat(program.ManagerUserIds).Distinct().ToList();
        return View(new AdminProgramViewModel
        {
            Program = program,
            Today = grants.Today,
            CanAdminister = (await authorization.AuthorizeAsync(User, program, ProgramOperationRequirement.Administer)).Succeeded,
            CanManage = (await authorization.AuthorizeAsync(User, program, ProgramOperationRequirement.Manage)).Succeeded,
            People = await UserService.GetUserInfosAsync(ids, ct)
        });
    }

    [HttpGet("{programId:guid}/Edit")]
    [Authorize(Policy = GrantsPolicies.Manage)]
    public async Task<IActionResult> Edit(Guid programId, CancellationToken ct)
    {
        var (program, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Administer, ct);
        if (forbidden is not null) return forbidden;
        return View("Form", await FillAsync(ProgramFormViewModel.From(program!), ct));
    }

    [HttpPost("{programId:guid}/Edit")]
    [Authorize(Policy = GrantsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid programId, ProgramFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        var (_, forbidden) = await AuthorizedAsync(programId, ProgramOperationRequirement.Administer, ct);
        if (forbidden is not null) return forbidden;
        model.Id = programId;
        if (!ModelState.IsValid) return View("Form", await FillAsync(model, ct));

        var result = await grants.UpdateProgramAsync(programId, model.ToSave(), user.Id, ct);
        if (result.Refusal is { } refusal)
        {
            ModelState.AddModelError(string.Empty, refusal);
            return View("Form", await FillAsync(model, ct));
        }
        SetSuccess("Program saved");
        return RedirectToAction(nameof(Program), new { programId });
    }

    [HttpPost("{programId:guid}/Issue")]
    [Authorize(Policy = GrantProgramsPolicy.Name)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Issue(Guid programId, Guid[] userIds, LocalDate? expiresOn, CancellationToken ct) =>
        ManageAsync(programId, async actor =>
        {
            var result = await grants.IssueToAsync(programId, userIds, expiresOn, actor, ct);
            return (result, $"{result.Value?.Count ?? 0} granted");
        }, ct);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Revoke")]
    [Authorize(Policy = GrantProgramsPolicy.Name)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Revoke(Guid programId, Guid grantId, string? reason, CancellationToken ct) =>
        ManageAsync(programId, async actor =>
            ((GrantMutationResult)await grants.RevokeGrantAsync(grantId, reason, actor, ct), "Grant revoked"), ct, grantId);

    [HttpPost("{programId:guid}/Grants/{grantId:guid}/Resend")]
    [Authorize(Policy = GrantProgramsPolicy.Name)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Resend(Guid programId, Guid grantId, CancellationToken ct) =>
        ManageAsync(programId, async actor =>
            ((GrantMutationResult)await grants.ResendNoticeAsync(grantId, actor, ct), "Notice sent again"), ct, grantId);

    /// <summary>Loads the program and checks the operation against it; 404 and 403 as results.</summary>
    private async Task<(ProgramInfo? Program, IActionResult? Error)> AuthorizedAsync(
        Guid programId, ProgramOperationRequirement requirement, CancellationToken ct)
    {
        var program = await grants.GetProgramAsync(programId, ct);
        if (program is null) return (null, NotFound());
        var allowed = await authorization.AuthorizeAsync(User, program, requirement);
        return allowed.Succeeded ? (program, null) : (null, Forbid());
    }

    /// <summary>A grant of another program is 404, so a manager of A never acts on B's grants.</summary>
    private async Task<IActionResult> ManageAsync(
        Guid programId, Func<Guid, Task<(GrantMutationResult Result, string Success)>> act, CancellationToken ct,
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

        var (result, success) = await act(user.Id);
        if (result.Refusal is { } refusal)
            SetError(refusal);
        else
            SetSuccess(success);
        return RedirectToAction(nameof(Program), new { programId });
    }

    private async Task<ProgramFormViewModel> FillAsync(ProgramFormViewModel model, CancellationToken ct)
    {
        model.People = await UserService.GetUserInfosAsync(model.ManagerUserIds, ct);
        model.Events = (await settings.GetAllEventSettingsAsync(ct))
            .Where(e => e.Status != EventSettingsStatus.Deleted)
            .OrderByDescending(e => e.GateOpeningDate)
            .ToList();
        return model;
    }
}

using Humans.Base.Controllers;
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
/// Member pages: my pre-approvals, and the programs open for applications. Gated by the
/// section's <see cref="PreapprovalsPolicies.Access"/> policy so a person with nothing here
/// never sees an empty page.
/// </summary>
[Authorize(Policy = PreapprovalsPolicies.Access)]
[Route("Preapprovals")]
internal sealed class PreapprovalsController(
    IPreapprovalManagementService preapprovals,
    IUserServiceRead users,
    IStringLocalizer<PreapprovalsResource> localizer,
    IAuthorizationService authorization,
    IClock clock,
    ILogger<PreapprovalsController> logger) : HumansControllerBase(users)
{
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private LocalDate Today => clock.GetCurrentInstant().InZone(MadridZone).Date;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var today = Today;
        var canManage = (await authorization.AuthorizeAsync(User, PreapprovalsPolicies.Manage)).Succeeded;
        return View(new MemberIndexViewModel
        {
            CurrentUserId = user.Id,
            Today = today,
            Grants = await preapprovals.GetGrantsForUserAsync(user.Id, ct),
            OpenPrograms = (await preapprovals.GetProgramsAsync(ct))
                .Where(p => p.IsAcceptingApplications(today)).OrderBy(p => p.ClaimBy).ToList(),
            CanManage = canManage
        });
    }

    [HttpGet("{programId:guid}")]
    public async Task<IActionResult> Program(Guid programId, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        var program = await preapprovals.GetProgramAsync(programId, ct);
        if (program is null) return NotFound();
        return View(await BuildProgramAsync(program, user.Id, new ApplyFormViewModel(), ct));
    }

    [HttpPost("{programId:guid}/Apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(Guid programId, ApplyFormViewModel form, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        var program = await preapprovals.GetProgramAsync(programId, ct);
        if (program is null) return NotFound();
        if (!ModelState.IsValid) return View(nameof(Program), await BuildProgramAsync(program, user.Id, form, ct));

        var result = await preapprovals.ApplyAsync(programId, user.Id, form.Note, form.RequestedAmount, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Preapprovals Apply: rule {Rule}", refusal.Key);
            // The form is hidden when the program is not accepting; a POST that arrives anyway is a bad request.
            if (refusal.Key is PreapprovalErrorKeys.ProgramClosed or PreapprovalErrorKeys.NotAcceptingApplications
                or PreapprovalErrorKeys.DeadlinePassed)
                return BadRequest(localizer[refusal.Key, refusal.Args].Value);
            SetError(localizer[refusal.Key, refusal.Args]);
            return RedirectToAction(nameof(Program), new { programId });
        }
        SetSuccess(localizer["Preapprovals_Applied"]);
        return RedirectToAction(nameof(Program), new { programId });
    }

    private async Task<MemberProgramViewModel> BuildProgramAsync(
        ProgramInfo program, Guid userId, ApplyFormViewModel form, CancellationToken ct) =>
        new()
        {
            Program = program,
            Today = Today,
            MyGrants = (await preapprovals.GetGrantsForUserAsync(userId, ct)).Where(g => g.ProgramId == program.Id).ToList(),
            Form = form
        };
}

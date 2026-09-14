using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Governance.Models;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Humans.Governance.Controllers;

/// <summary>
/// Board/Admin assembly-vote administration: drafting and the lifecycle (open, stop, extend,
/// cancel, peek, post-close ballots list). Drafting is <see cref="PolicyNames.BoardOrAdmin"/>;
/// every lifecycle action that changes what the electorate sees or reads the embargoed tally is
/// <see cref="PolicyNames.AdminOnly"/> for the first votes (Docs/features/assembly-votes.md,
/// "Decisions" #1) — Board gets read access to everything, including post-close ballots.
/// Admin routes are localization-exempt, so flash and validation text here is plain English.
/// </summary>
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Governance/Votes/Admin")]
internal sealed class GovernanceVotesAdminController(
    IUserServiceRead userService,
    IAssemblyVoteService voteService,
    ILogger<GovernanceVotesAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var votes = await voteService.GetAllForAdminAsync(ct);
        return View("~/Views/Governance/Votes/Admin/Index.cshtml", new AssemblyVoteAdminListViewModel { Votes = votes });
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View("~/Views/Governance/Votes/Admin/Create.cshtml", new AssemblyVoteDraftFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        AssemblyVoteDraftFormViewModel model, string? submitAction, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var voteId = await voteService.CreateDraftAsync(model.ToDraft(), actorId, ct);
        if (voteId is null)
        {
            SetError("That draft was rejected — check the required fields and options.");
            return View("~/Views/Governance/Votes/Admin/Create.cshtml", model);
        }

        // The draft is saved at this point — a translation failure must not re-render the form
        // as unsaved, or a re-submit would create a second draft. It reports and redirects.
        if (IsTranslate(submitAction))
        {
            await TranslateAsync(voteId.Value, actorId, "Draft created", ct);
            return RedirectToAction(nameof(Edit), new { voteId = voteId.Value });
        }

        SetSuccess("Draft created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{voteId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid voteId, CancellationToken ct)
    {
        var draft = await voteService.GetDraftAsync(voteId, ct);
        if (draft is null) return NotFound();

        return View("~/Views/Governance/Votes/Admin/Edit.cshtml", AssemblyVoteDraftFormViewModel.FromDraft(voteId, draft));
    }

    [HttpPost("{voteId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid voteId, AssemblyVoteDraftFormViewModel model, string? submitAction, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var result = await voteService.UpdateDraftAsync(voteId, model.ToDraft(), actorId, ct);
        if (result == AssemblyVoteActionResult.Ok && IsTranslate(submitAction))
        {
            // Saved already, so a translation failure reports rather than re-rendering.
            await TranslateAsync(voteId, actorId, "Draft updated", ct);
            return RedirectToAction(nameof(Edit), new { voteId });
        }

        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Draft updated.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => RedisplayEdit(voteId, model, "This vote is no longer a draft — content is locked once it opens."),
            _ => RedisplayEdit(voteId, model, "That draft was rejected — check the required fields and options.")
        };
    }

    [HttpPost("{voteId:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var result = await voteService.DeleteDraftAsync(voteId, actorId, ct);
        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Draft deleted.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => Error("Only a draft can be deleted.", nameof(Index)),
            _ => Error("That delete was rejected.", nameof(Index))
        };
    }

    [HttpPost("{voteId:guid}/Open")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Open(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } adminId) return Challenge();

        var result = await voteService.OpenAsync(voteId, adminId, ct);
        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Vote opened.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => Error("Only a draft can be opened.", nameof(Index)),
            _ => Error("That draft could not be opened.", nameof(Index))
        };
    }

    [HttpPost("{voteId:guid}/Stop")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Stop(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } adminId) return Challenge();

        var result = await voteService.StopAsync(voteId, adminId, ct);
        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Vote closed.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => Error("Only an open vote can be stopped.", nameof(Index)),
            _ => Error("That vote could not be stopped.", nameof(Index))
        };
    }

    [HttpPost("{voteId:guid}/Extend")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Extend(Guid voteId, AssemblyVoteExtendFormModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } adminId) return Challenge();

        var result = await voteService.ExtendAsync(voteId, model.ToInstant(), adminId, ct);
        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Closing time extended.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => Error("Only an open vote can be extended.", nameof(Index)),
            _ => Error("The new closing time must be later than the current one.", nameof(Index))
        };
    }

    [HttpPost("{voteId:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Cancel(Guid voteId, AssemblyVoteCancelFormModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } adminId) return Challenge();

        if (string.IsNullOrWhiteSpace(model.Reason))
            return Error("A cancellation reason is required.", nameof(Index));

        var result = await voteService.CancelAsync(voteId, model.Reason, adminId, ct);
        return result switch
        {
            AssemblyVoteActionResult.Ok => Success("Vote cancelled.", nameof(Index)),
            AssemblyVoteActionResult.NotFound => NotFound(),
            AssemblyVoteActionResult.WrongState => Error("Only an open vote can be cancelled.", nameof(Index)),
            _ => Error("A cancellation reason is required.", nameof(Index))
        };
    }

    /// <summary>Live tally for an Admin making a call at the assembly. Every call is audited by the service, choice content included.</summary>
    [HttpGet("{voteId:guid}/Peek")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Peek(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } adminId) return Challenge();

        var (result, recorded) = await voteService.PeekAsync(voteId, adminId, ct);
        if (result is null) return NotFound();

        // The vote closed before the click landed (or the admin typed the URL). Nothing was
        // logged, so the ordinary results page is both the honest destination and the one
        // that already shows this tally.
        if (!recorded)
        {
            return RedirectToAction(
                "Results", "GovernanceVotes", new { voteId });
        }

        // The counting result is keyed by option key; the member-facing read supplies the
        // labels so the rounds table is readable out loud at the assembly. It carries no
        // tally of its own, so it adds nothing to what the peek already disclosed.
        var vote = await voteService.GetVoteForMemberAsync(voteId, adminId, ct);

        return View("~/Views/Governance/Votes/Admin/Peek.cshtml", new AssemblyVotePeekViewModel
        {
            VoteId = voteId,
            Result = result,
            Options = vote?.Options ?? []
        });
    }

    [HttpGet("{voteId:guid}/Ballots")]
    public async Task<IActionResult> Ballots(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var ballots = await voteService.GetBallotsForBoardAsync(voteId, actorId, ct);
        if (ballots is null) return NotFound();

        // Ballots store option keys; the member-facing read supplies the labels.
        var vote = await voteService.GetVoteForMemberAsync(voteId, actorId, ct);

        return View("~/Views/Governance/Votes/Admin/Ballots.cshtml", new AssemblyVoteBallotsViewModel
        {
            VoteId = voteId,
            Ballots = ballots,
            Options = vote?.Options ?? []
        });
    }

    /// <summary>The draft forms' second submit button: save, then fill the empty cultures.</summary>
    private const string TranslateAction = "save-translate";

    private static bool IsTranslate(string? submitAction) =>
        string.Equals(submitAction, TranslateAction, StringComparison.Ordinal);

    /// <summary>
    /// Machine-fills the cultures the author left blank from the vote's official culture, and
    /// flashes what happened. Never throws: the draft is already saved by the time it runs.
    /// </summary>
    private async Task TranslateAsync(Guid voteId, Guid actorId, string saved, CancellationToken ct)
    {
        try
        {
            var filled = await voteService.PreFillTranslationsAsync(
                voteId, CultureCatalog.SupportedCultureCodes, actorId, ct);
            SetSuccess(filled > 0
                ? $"{saved}; {filled} missing translation(s) pre-filled from the official culture — review them before opening."
                : $"{saved} — no missing translations to fill.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assembly vote {VoteId}: translation pre-fill failed", voteId);
            SetError($"{saved}, but translation failed: {ex.Message}");
        }
    }

    private IActionResult Success(string message, string action)
    {
        SetSuccess(message);
        return RedirectToAction(action);
    }

    private IActionResult Error(string message, string action)
    {
        SetError(message);
        return RedirectToAction(action);
    }

    private IActionResult RedisplayEdit(Guid voteId, AssemblyVoteDraftFormViewModel model, string message)
    {
        SetError(message);
        model.VoteId = voteId;
        return View("~/Views/Governance/Votes/Admin/Edit.cshtml", model);
    }
}

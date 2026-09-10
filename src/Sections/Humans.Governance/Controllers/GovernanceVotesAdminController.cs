using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Governance.Models;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    IAssemblyVoteService voteService) : HumansControllerBase(userService)
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
    public async Task<IActionResult> Create(AssemblyVoteDraftFormViewModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var voteId = await voteService.CreateDraftAsync(model.ToDraft(), actorId, ct);
        if (voteId is null)
        {
            SetError("That draft was rejected — check the required fields and options.");
            return View("~/Views/Governance/Votes/Admin/Create.cshtml", model);
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
    public async Task<IActionResult> Edit(Guid voteId, AssemblyVoteDraftFormViewModel model, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var result = await voteService.UpdateDraftAsync(voteId, model.ToDraft(), actorId, ct);
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
            AssemblyVoteActionResult.Ok => Success("Vote opened — the roster has been notified.", nameof(Index)),
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
            AssemblyVoteActionResult.Ok => Success("Vote cancelled — the roster has been notified.", nameof(Index)),
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

        var result = await voteService.PeekAsync(voteId, adminId, ct);
        if (result is null) return NotFound();

        return View("~/Views/Governance/Votes/Admin/Peek.cshtml", new AssemblyVotePeekViewModel { VoteId = voteId, Result = result });
    }

    [HttpGet("{voteId:guid}/Ballots")]
    public async Task<IActionResult> Ballots(Guid voteId, CancellationToken ct)
    {
        if (GetCurrentUserId() is not { } actorId) return Challenge();

        var ballots = await voteService.GetBallotsForBoardAsync(voteId, actorId, ct);
        if (ballots is null) return NotFound();

        return View("~/Views/Governance/Votes/Admin/Ballots.cshtml", new AssemblyVoteBallotsViewModel { VoteId = voteId, Ballots = ballots });
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

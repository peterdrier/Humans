using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Rideshare.Controllers;

/// <summary>
/// Member-facing Rideshare: the board, own offers/requests, and the interest lifecycle.
/// Ownership and state rules live in <see cref="IRideshareService"/>; this only maps its
/// error contract (404 / 403 / message) onto the response.
/// </summary>
[Authorize(Policy = PolicyNames.AppAccess)]
[Route("Rideshare")]
internal sealed class RideshareController(
    IRideshareService rideshare,
    IUserServiceRead users,
    IStringLocalizer<RideshareResource> localizer,
    IClock clock,
    ILogger<RideshareController> logger) : HumansControllerBase(users)
{
    // ── Board ─────────────────────────────────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> Index(string? date, RideshareDirection? direction, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        var dir = direction ?? RideshareDirection.Inbound;
        var day = RideshareDates.Parse(date) ?? BoardViewModel.DefaultDate(snapshot.Settings, dir, RideshareDates.Today(clock));

        return View(BoardViewModel.Build(snapshot, day, dir, user.Id));
    }

    // ── Offers ────────────────────────────────────────────────────────────

    [HttpGet("Offer")]
    public async Task<IActionResult> Offer(Guid? id, RideshareDirection? direction, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        if (id is null)
            return View(OfferFormViewModel.ForNew(user, direction ?? RideshareDirection.Inbound, snapshot.Settings, RideshareDates.Today(clock)));

        var trip = snapshot.Trips.FirstOrDefault(t => t.Id == id);
        if (trip is null) return NotFound();
        if (trip.UserId != user.Id) return Forbid();
        return View(OfferFormViewModel.FromTrip(trip));
    }

    [HttpPost("Offer")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Offer(OfferFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (!ModelState.IsValid) return View(model);
        var save = model.ToSave();
        if (save is null)
        {
            ModelState.AddModelError(nameof(model.DepartureDate), localizer["Rideshare_InvalidDate"]);
            return View(model);
        }

        return await SaveFormAsync(model, async () =>
        {
            if (model.Id is { } id) await rideshare.UpdateOfferAsync(id, user.Id, save, ct);
            else await rideshare.CreateOfferAsync(user.Id, await rideshare.GetActiveYearAsync(ct), save, ct);
        }, "Rideshare_OfferSaved");
    }

    [HttpPost("Offer/{id:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelOffer(Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return await RunThenMineAsync(() => rideshare.CancelOfferAsync(id, user.Id, ct), "Rideshare_OfferCancelled");
    }

    // ── Requests ──────────────────────────────────────────────────────────

    // Named RideRequest rather than Request: ControllerBase.Request is the HTTP request.
    [HttpGet("Request")]
    public async Task<IActionResult> RideRequest(Guid? id, RideshareDirection? direction, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        if (id is null)
            return View(RequestFormViewModel.ForNew(user, direction ?? RideshareDirection.Inbound, snapshot.Settings, RideshareDates.Today(clock)));

        var request = snapshot.Requests.FirstOrDefault(r => r.Id == id);
        if (request is null) return NotFound();
        if (request.UserId != user.Id) return Forbid();
        return View(RequestFormViewModel.FromRequest(request));
    }

    [HttpPost("Request")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RideRequest(RequestFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (!ModelState.IsValid) return View(model);
        var save = model.ToSave();
        if (save is null)
        {
            ModelState.AddModelError(nameof(model.DesiredDate), localizer["Rideshare_InvalidDate"]);
            return View(model);
        }

        return await SaveFormAsync(model, async () =>
        {
            if (model.Id is { } id) await rideshare.UpdateRequestAsync(id, user.Id, save, ct);
            else await rideshare.CreateRequestAsync(user.Id, await rideshare.GetActiveYearAsync(ct), save, ct);
        }, "Rideshare_RequestSaved");
    }

    [HttpPost("Request/{id:guid}/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelRequest(Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return await RunThenMineAsync(() => rideshare.CancelRequestAsync(id, user.Id, ct), "Rideshare_RequestCancelled");
    }

    // ── Mine ──────────────────────────────────────────────────────────────

    [HttpGet("Mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        return View(MineViewModel.Build(snapshot, user.Id));
    }

    // ── Interests ─────────────────────────────────────────────────────────

    /// <summary>"I'm interested" (rider → trip) or "I can take you" (driver's trip → request pin).</summary>
    [HttpPost("Interest")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Interest(Guid tripId, Guid? requestId, int seats, string? message, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (tripId == Guid.Empty)
        {
            SetError(localizer["Rideshare_PickAnOffer"]);
            return RedirectToAction(nameof(Index));
        }

        return await RunThenMineAsync(
            () => rideshare.ExpressInterestAsync(user.Id, tripId, requestId, seats, message, ct),
            "Rideshare_InterestSent");
    }

    [HttpPost("Interest/{id:guid}/Accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return await RunThenMineAsync(() => rideshare.AcceptInterestAsync(id, user.Id, ct), "Rideshare_InterestAccepted");
    }

    [HttpPost("Interest/{id:guid}/Decline")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return await RunThenMineAsync(() => rideshare.DeclineInterestAsync(id, user.Id, ct), "Rideshare_InterestDeclined");
    }

    [HttpPost("Interest/{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return await RunThenMineAsync(() => rideshare.WithdrawInterestAsync(id, user.Id, ct), "Rideshare_InterestWithdrawn");
    }

    // ── Error-contract mapping ────────────────────────────────────────────

    /// <summary>Redirect-style actions: 404 / 403 pass through, a rule becomes a localized error toast on Mine.</summary>
    private async Task<IActionResult> RunThenMineAsync(Func<Task> action, string successKey)
    {
        try
        {
            await action();
            SetSuccess(localizer[successKey]);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Rideshare {Action}: not found", ControllerContext.ActionDescriptor.ActionName);
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Rideshare {Action}: forbidden", ControllerContext.ActionDescriptor.ActionName);
            return Forbid();
        }
        catch (RideshareRuleException ex)
        {
            logger.LogInformation(ex, "Rideshare {Action}: rule {Rule}", ControllerContext.ActionDescriptor.ActionName, ex.Key);
            SetError(localizer[ex.Key, ex.Args]);
        }

        return RedirectToAction(nameof(Mine));
    }

    /// <summary>Form POSTs: a rule re-renders the form with its localized message as a model error.</summary>
    private async Task<IActionResult> SaveFormAsync(object model, Func<Task> action, string successKey)
    {
        try
        {
            await action();
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Rideshare {Action}: not found", ControllerContext.ActionDescriptor.ActionName);
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Rideshare {Action}: forbidden", ControllerContext.ActionDescriptor.ActionName);
            return Forbid();
        }
        catch (RideshareRuleException ex)
        {
            logger.LogInformation(ex, "Rideshare {Action}: rule {Rule}", ControllerContext.ActionDescriptor.ActionName, ex.Key);
            ModelState.AddModelError(string.Empty, localizer[ex.Key, ex.Args]);
            return View(model);
        }

        SetSuccess(localizer[successKey]);
        return RedirectToAction(nameof(Mine));
    }
}

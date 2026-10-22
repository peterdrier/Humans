using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Models;
using Humans.Rideshare.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Rideshare.Controllers;

[Authorize(Policy = PolicyNames.AppAccess)]
[Route("Rideshare")]
internal sealed class RideshareController(
    IRideshareService rideshare,
    IUserService users,
    IStringLocalizer<RideshareResource> localizer,
    IClock clock,
    ILogger<RideshareController> logger) : HumansControllerBase(users)
{
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
            if (model.Id is { } id) return await rideshare.UpdateOfferAsync(id, user.Id, save, ct);
            else return await rideshare.CreateOfferAsync(user.Id, await rideshare.GetActiveYearAsync(ct), save, ct);
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
            if (model.Id is { } id) return await rideshare.UpdateRequestAsync(id, user.Id, save, ct);
            else return await rideshare.CreateRequestAsync(user.Id, await rideshare.GetActiveYearAsync(ct), save, ct);
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

    [HttpGet("Mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var snapshot = await rideshare.GetSnapshotAsync(await rideshare.GetActiveYearAsync(ct), ct);
        return View(MineViewModel.Build(snapshot, user.Id));
    }

    /// <summary>"I'm interested" (rider → trip) or "I can take you" (driver's trip → request pin).</summary>
    [HttpPost("Interest")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Interest(Guid tripId, Guid? requestId, int? seats, string? message, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        // A cleared seats box posts "seats=", which binds to null and takes the party-size default below.
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed rideshare interest form for user {UserId}", user.Id);
            return BadRequest(ModelState);
        }

        if (tripId == Guid.Empty)
        {
            SetError(localizer["Rideshare_PickAnOffer"]);
            return RedirectToAction(nameof(Index));
        }

        return await RunThenMineAsync(
            () => rideshare.ExpressInterestAsync(user.Id, tripId, requestId, seats ?? 0, message, ct),
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

    /// <summary>Redirect-style actions: 404 / 403 pass through, a rule becomes a localized error toast on Mine.</summary>
    private async Task<IActionResult> RunThenMineAsync(Func<Task<RideshareMutationResult>> action, string successKey)
    {
        try
        {
            var result = await action();
            if (result.Refusal is { } refusal)
            {
                logger.LogWarning("Rideshare {Action}: rule {Rule}", ControllerContext.ActionDescriptor.ActionName, refusal.Key);
                SetError(localizer[refusal.Key, refusal.Args]);
            }
            else
            {
                SetSuccess(localizer[successKey]);
            }
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning("Rideshare {Action}: not found ({Reason})", ControllerContext.ActionDescriptor.ActionName, ex.Message);
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Rideshare {Action}: forbidden ({Reason})", ControllerContext.ActionDescriptor.ActionName, ex.Message);
            return Forbid();
        }

        return RedirectToAction(nameof(Mine));
    }

    /// <summary>Form POSTs: a rule re-renders the form with its localized message as a model error.</summary>
    private async Task<IActionResult> SaveFormAsync(object model, Func<Task<RideshareMutationResult>> action, string successKey)
    {
        try
        {
            var result = await action();
            if (result.Refusal is { } refusal)
            {
                logger.LogWarning("Rideshare {Action}: rule {Rule}", ControllerContext.ActionDescriptor.ActionName, refusal.Key);
                ModelState.AddModelError(string.Empty, localizer[refusal.Key, refusal.Args]);
                return View(model);
            }
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning("Rideshare {Action}: not found ({Reason})", ControllerContext.ActionDescriptor.ActionName, ex.Message);
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Rideshare {Action}: forbidden ({Reason})", ControllerContext.ActionDescriptor.ActionName, ex.Message);
            return Forbid();
        }

        SetSuccess(localizer[successKey]);
        return RedirectToAction(nameof(Mine));
    }
}

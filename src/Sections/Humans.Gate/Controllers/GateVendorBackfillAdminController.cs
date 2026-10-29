using Humans.Gate.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Gate.Controllers;

// One-off backfill; remove after use. Vendor check-ins double-record, so both POSTs recompute the diff server-side
// and GateVendorMirrorLedger keeps already-enqueued ids out until the ticket sync confirms them.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Gate/Admin/VendorCheckInBackfill")]
internal sealed class GateVendorBackfillAdminController(
    IUserService users,
    IGateService gate) : HumansControllerBase(users)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(await gate.GetVendorCheckInBackfillAsync(ct));

    // One check-in, to verify on the vendor dashboard before the bulk run.
    [HttpPost("RunOne")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunOne(string vendorTicketId, CancellationToken ct)
    {
        var result = await gate.EnqueueVendorCheckInBackfillAsync(vendorTicketId ?? string.Empty, ct);
        if (!result.MirrorEnabled)
            SetMirrorDisabledError();
        else if (result.Enqueued.FirstOrDefault() is { } row)
        {
            SetSuccess($"Test check-in enqueued for {row.AttendeeName ?? row.Barcode} ({row.VendorTicketId}). " +
                       "Verify it on the TicketTailor dashboard, then send the rest.");
        }
        else
            SetError("That ticket is no longer pending — already sent, checked in at the vendor, or unknown.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        var result = await gate.EnqueueVendorCheckInBackfillAsync(null, ct);
        if (!result.MirrorEnabled)
            SetMirrorDisabledError();
        else
            SetSuccess($"Enqueued {result.Enqueued.Count} vendor check-in(s). " +
                       "Sent rows are excluded from re-sending; counts update after the next ticket sync.");
        return RedirectToAction(nameof(Index));
    }

    private void SetMirrorDisabledError() =>
        SetError("Gate:VendorMirrorEnabled is off — enqueued jobs would silently skip. Set it in the environment first.");
}

using Hangfire;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

// One-off vendor check-in backfill (temp page, remove after use). Recovers gate admits that
// GateVendorCheckInJob never mirrored to TicketTailor while Gate:VendorMirrorEnabled was unset:
// shows the diff of local admits vs the vendor's check-in status (last ticket sync), lets an
// admin send a single test check-in first, then the whole pending set. Both POSTs recompute
// the diff server-side — a client-supplied id is never enqueued blindly (TicketTailor
// check-ins double-record, so rows already checked in at the vendor must drop out).
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Gate/Admin/VendorCheckInBackfill")]
public sealed class GateVendorBackfillAdminController(
    IUserServiceRead users,
    IGateService gate,
    IConfiguration configuration) : HumansControllerBase(users)
{
    private const string MirrorEnabledKey = "Gate:VendorMirrorEnabled";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await gate.GetVendorCheckInBackfillAsync(ct);
        return View(new GateVendorBackfillViewModel(
            snapshot, MirrorEnabled: configuration.GetValue<bool>(MirrorEnabledKey)));
    }

    // Send ONE pending check-in so the result can be verified on the vendor dashboard
    // before the bulk run.
    [HttpPost("RunOne")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunOne(string vendorTicketId, CancellationToken ct)
    {
        if (MirrorDisabledError() is { } error)
            return error;

        var snapshot = await gate.GetVendorCheckInBackfillAsync(ct);
        var row = snapshot.Pending.FirstOrDefault(r =>
            string.Equals(r.VendorTicketId, vendorTicketId, StringComparison.Ordinal));
        if (row is null)
        {
            SetError("That ticket is no longer pending — already checked in at the vendor, or unknown.");
            return RedirectToAction(nameof(Index));
        }

        BackgroundJob.Enqueue<GateVendorCheckInJob>(j => j.ExecuteAsync(row.VendorTicketId!, CancellationToken.None));
        SetSuccess($"Test check-in enqueued for {row.AttendeeName ?? row.Barcode} ({row.VendorTicketId}). " +
                   "Verify it on the TicketTailor dashboard, then send the rest.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        if (MirrorDisabledError() is { } error)
            return error;

        var snapshot = await gate.GetVendorCheckInBackfillAsync(ct);
        foreach (var row in snapshot.Pending)
            BackgroundJob.Enqueue<GateVendorCheckInJob>(j => j.ExecuteAsync(row.VendorTicketId!, CancellationToken.None));

        SetSuccess($"Enqueued {snapshot.Pending.Count} vendor check-in(s). " +
                   "Counts on this page update after the next ticket sync — do not re-run before it completes.");
        return RedirectToAction(nameof(Index));
    }

    private IActionResult? MirrorDisabledError()
    {
        if (configuration.GetValue<bool>(MirrorEnabledKey))
            return null;
        SetError("Gate:VendorMirrorEnabled is off — enqueued jobs would silently skip. Set it in the environment first.");
        return RedirectToAction(nameof(Index));
    }
}

public sealed record GateVendorBackfillViewModel(GateVendorBackfillSnapshot Snapshot, bool MirrorEnabled);

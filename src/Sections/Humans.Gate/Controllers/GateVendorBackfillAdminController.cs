using Hangfire;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Gate.Jobs;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Gate.Controllers;

// One-off backfill; remove after use. Vendor check-ins double-record, so both POSTs recompute the diff server-side
// and GateVendorMirrorLedger keeps already-enqueued ids out until the ticket sync confirms them.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Gate/Admin/VendorCheckInBackfill")]
internal sealed class GateVendorBackfillAdminController(
    IUserService users,
    IGateService gate,
    IConfiguration configuration,
    GateVendorMirrorLedger ledger,
    IBackgroundJobClient backgroundJobs) : HumansControllerBase(users)
{
    private const string MirrorEnabledKey = "Gate:VendorMirrorEnabled";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (snapshot, pending, sent) = await GetPendingSplitAsync(ct);
        return View(new GateVendorBackfillViewModel(
            snapshot, pending, sent, MirrorEnabled: configuration.GetValue<bool>(MirrorEnabledKey)));
    }

    // One check-in, to verify on the vendor dashboard before the bulk run.
    [HttpPost("RunOne")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunOne(string vendorTicketId, CancellationToken ct)
    {
        if (MirrorDisabledError() is { } error)
            return error;

        var (_, pending, _) = await GetPendingSplitAsync(ct);
        var row = pending.FirstOrDefault(r =>
            string.Equals(r.VendorTicketId, vendorTicketId, StringComparison.Ordinal));
        if (row is null || !TryEnqueue(row))
        {
            SetError("That ticket is no longer pending — already sent, checked in at the vendor, or unknown.");
            return RedirectToAction(nameof(Index));
        }

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

        var (_, pending, _) = await GetPendingSplitAsync(ct);
        var enqueued = pending.Count(TryEnqueue);

        SetSuccess($"Enqueued {enqueued} vendor check-in(s). " +
                   "Sent rows are excluded from re-sending; counts update after the next ticket sync.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<(GateVendorBackfillSnapshot Snapshot, IReadOnlyList<GateVendorBackfillRow> Pending, IReadOnlyList<GateVendorBackfillRow> Sent)>
        GetPendingSplitAsync(CancellationToken ct)
    {
        var snapshot = await gate.GetVendorCheckInBackfillAsync(ct);
        var pending = snapshot.Pending.Where(r => !ledger.WasSent(r.VendorTicketId!)).ToList();
        var sent = snapshot.Pending.Where(r => ledger.WasSent(r.VendorTicketId!)).ToList();
        return (snapshot, pending, sent);
    }

    // ExecuteBackfillAsync records the original admit time. The ledger claim is atomic and precedes the enqueue, so
    // overlapping requests never both enqueue the same non-idempotent check-in.
    private bool TryEnqueue(GateVendorBackfillRow row)
    {
        var vendorTicketId = row.VendorTicketId!;
        if (!ledger.TryMarkSent(vendorTicketId))
            return false;

        var admittedAtUnixSeconds = row.AdmittedAt.ToUnixTimeSeconds();
        backgroundJobs.Enqueue<GateVendorCheckInJob>(j =>
            j.ExecuteBackfillAsync(vendorTicketId, admittedAtUnixSeconds, CancellationToken.None));
        return true;
    }

    private IActionResult? MirrorDisabledError()
    {
        if (configuration.GetValue<bool>(MirrorEnabledKey))
            return null;
        SetError("Gate:VendorMirrorEnabled is off — enqueued jobs would silently skip. Set it in the environment first.");
        return RedirectToAction(nameof(Index));
    }
}

internal sealed record GateVendorBackfillViewModel(
    GateVendorBackfillSnapshot Snapshot,
    IReadOnlyList<GateVendorBackfillRow> Pending,
    IReadOnlyList<GateVendorBackfillRow> SentAwaitingSync,
    bool MirrorEnabled);

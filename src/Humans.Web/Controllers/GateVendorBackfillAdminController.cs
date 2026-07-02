using Hangfire;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Jobs;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

// One-off vendor check-in backfill (temp page, remove after use). Recovers gate admits that
// GateVendorCheckInJob never mirrored to TicketTailor while Gate:VendorMirrorEnabled was unset:
// shows the diff of local admits vs the vendor's check-in status (last ticket sync), lets an
// admin send a single test check-in first, then the whole pending set. TicketTailor check-ins
// double-record on repeat, so two guards apply: both POSTs recompute the diff server-side (a
// client-supplied id is never enqueued blindly), and GateVendorMirrorLedger keeps every
// already-enqueued id out of both send paths until the ticket sync confirms it.
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Gate/Admin/VendorCheckInBackfill")]
public sealed class GateVendorBackfillAdminController(
    IUserServiceRead users,
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

    // Send ONE pending check-in so the result can be verified on the vendor dashboard
    // before the bulk run.
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

    // Pending split into not-yet-sent vs sent-awaiting-sync (the ledger remembers enqueued
    // ids until the vendor's check-in flows back through the ticket sync).
    private async Task<(GateVendorBackfillSnapshot Snapshot, IReadOnlyList<GateVendorBackfillRow> Pending, IReadOnlyList<GateVendorBackfillRow> Sent)>
        GetPendingSplitAsync(CancellationToken ct)
    {
        var snapshot = await gate.GetVendorCheckInBackfillAsync(ct);
        var pending = snapshot.Pending.Where(r => !ledger.WasSent(r.VendorTicketId!)).ToList();
        var sent = snapshot.Pending.Where(r => ledger.WasSent(r.VendorTicketId!)).ToList();
        return (snapshot, pending, sent);
    }

    // ExecuteBackfillAsync (not the live ExecuteAsync) so TicketTailor records the ORIGINAL
    // gate admit time as check_in_at, not the moment the admin clicked Send. The ledger claim
    // is atomic and happens BEFORE the enqueue: two overlapping requests (double-click, second
    // admin) must never both enqueue the same non-idempotent vendor check-in.
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

public sealed record GateVendorBackfillViewModel(
    GateVendorBackfillSnapshot Snapshot,
    IReadOnlyList<GateVendorBackfillRow> Pending,
    IReadOnlyList<GateVendorBackfillRow> SentAwaitingSync,
    bool MirrorEnabled);

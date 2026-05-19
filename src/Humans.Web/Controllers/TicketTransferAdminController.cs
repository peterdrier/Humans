using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Transfers")]
public sealed class TicketTransferAdminController(
    ITicketTransferService service,
    ITicketQueryService ticketQueryService,
    IUserService userService,
    ILogger<TicketTransferAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct)
    {
        tab ??= "pending";

        var pendingAll = await service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        var pending = pendingAll.OrderBy(r => r.RequestedAt).ToList();

        var approvedAll = await service.GetByStatusAsync(TicketTransferStatus.Approved, ct);
        var needsAttention = approvedAll
            .Where(r => r.VendorResult == TicketTransferVendorResult.Failed
                     || r.VendorResult == TicketTransferVendorResult.VoidSucceededIssueFailed)
            .OrderByDescending(r => r.DecidedAt)
            .ToList();

        IReadOnlyList<TicketTransferRowDto> rows = tab switch
        {
            "needs-attention" => needsAttention,
            "all" => await BuildAllAsync(ct),
            _ => pending,
        };

        var drift = (await ticketQueryService.GetOrderDriftAsync(ct))
            .OrderByDescending(r => r.IssuedCount - r.ValidCount)
            .ToList();

        return View(new TicketTransferIndexViewModel(
            ActiveTab: tab,
            PendingCount: pending.Count,
            NeedsAttentionCount: needsAttention.Count,
            Rows: rows,
            Drift: drift));
    }

    private async Task<IReadOnlyList<TicketTransferRowDto>> BuildAllAsync(CancellationToken ct)
    {
        var statuses = Enum.GetValues<TicketTransferStatus>();
        var combined = new List<TicketTransferRowDto>();
        foreach (var s in statuses)
            combined.AddRange(await service.GetByStatusAsync(s, ct));
        return combined.OrderByDescending(r => r.RequestedAt).ToList();
    }

    [HttpGet("Detail/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var detail = await service.GetDetailAsync(id, ct);
        if (detail is null)
        {
            return NotFound();
        }
        return View(detail);
    }

    [HttpPost("Decide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(
        Guid id, bool approve, string? adminNotes, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var result = await DispatchDecisionAsync(id, approve, user.Id, adminNotes, ct);
            ApplyDecisionFeedback(approve, result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Decide rejected for transfer {TransferId} (approve={Approve}): {Message}",
                id, approve, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/RetryIssue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryIssue(
        Guid id, string? adminNotes, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            var result = await service.RetryIssueAsync(id, user.Id, adminNotes, ct);
            if (result.VendorResult == TicketTransferVendorResult.Succeeded)
            {
                SetSuccess($"Retry succeeded — new ticket {result.VendorMessage ?? result.Id.ToString()}.");
            }
            else
            {
                SetError($"Retry failed: {result.VendorMessage}.");
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Retry-issue rejected for transfer {TransferId}: {Message}",
                id, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    private void ApplyDecisionFeedback(bool approve, TicketTransferRowDto result)
    {
        if (!approve)
        {
            SetSuccess("Transfer rejected.");
            return;
        }
        if (result.VendorResult == TicketTransferVendorResult.Succeeded)
        {
            SetSuccess("Transfer approved.");
            return;
        }
        // Vendor partial failure — manual finish required.
        SetError($"Transfer approved, but vendor writeback {result.VendorResult}. " +
            "Complete the transfer in the TicketTailor dashboard.");
    }

    private Task<TicketTransferRowDto> DispatchDecisionAsync(Guid id, bool approve, Guid userId, string? notes, CancellationToken ct) =>
        approve
            ? service.ApproveAsync(id, userId, notes, ct)
            : service.RejectAsync(id, userId, notes, ct);
}

using Humans.Tickets.Services;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Tickets.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Tickets.Domain;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Transfers")]
internal sealed class TicketTransferAdminController(
    ITicketTransferService service,
    ITicketService ticketQueryService,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct)
    {
        tab ??= "pending";

        var pending = (await service.GetByStatusAsync(TicketTransferStatus.Pending, ct))
            .OrderBy(r => r.RequestedAt)
            .ToList();

        IReadOnlyList<TicketTransferRowDto> rows = pending;
        if (string.Equals(tab, "all", StringComparison.Ordinal))
        {
            var combined = new List<TicketTransferRowDto>();
            foreach (var status in Enum.GetValues<TicketTransferStatus>())
                combined.AddRange(await service.GetByStatusAsync(status, ct));

            rows = combined.OrderByDescending(r => r.RequestedAt).ToList();
        }

        var drift = (await ticketQueryService.GetOrderDriftAsync(ct))
            .OrderByDescending(r => r.IssuedCount - r.ValidCount)
            .ToList();

        return View(new TicketTransferIndexViewModel(
            ActiveTab: tab,
            PendingCount: pending.Count,
            Rows: rows,
            Drift: drift));
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
        Guid id, string action, string? adminNotes, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        TicketTransferMutationResult result;
        string success;
        switch (action)
        {
            // Vendor writes stay detached: leaving between void and reissue must not
            // strand the receiver. The two local-only decisions keep the request token.
            case "process":
                result = await service.ProcessTransferAsync(id, user.Id, adminNotes, CancellationToken.None);
                success = "Transfer processed: ticket voided and reissued. The next sync confirms the local records.";
                break;
            case "retry":
                result = await service.RetryReissueAsync(id, user.Id, adminNotes, CancellationToken.None);
                success = "Reissue retried: the replacement ticket was issued to the receiver.";
                break;
            case "marksuccessful":
                result = await service.ApproveAsync(id, user.Id, adminNotes, ct);
                success = "Transfer marked successful.";
                break;
            case "cancel":
                result = await service.RejectAsync(id, user.Id, adminNotes ?? string.Empty, ct);
                success = "Transfer cancelled.";
                break;
            default:
                SetError("Unknown transfer action.");
                return RedirectToAction(nameof(Detail), new { id });
        }

        if (!result.Succeeded)
        {
            SetError(result.OperatorRefusal!);
            return RedirectToAction(nameof(Detail), new { id });
        }
        SetSuccess(success);
        return RedirectToAction(nameof(Index));
    }
}

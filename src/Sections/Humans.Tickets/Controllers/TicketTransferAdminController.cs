using Humans.Tickets.Services;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
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
    IUserServiceRead userService,
    ILogger<TicketTransferAdminController> logger) : HumansControllerBase(userService)
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

        try
        {
            switch (action)
            {
                // "process" and "retry" write to TicketTailor (void, then
                // reissue). Deliberately not passing the request-scoped token:
                // an admin navigating away between the void and the reissue
                // would leave the receiver with no ticket at all
                // (nobodies-collective/Humans#950). The two local-only branches
                // below keep it — a torn DB write rolls back.
                case "process":
                    await service.ProcessTransferAsync(id, user.Id, adminNotes, CancellationToken.None);
                    SetSuccess("Transfer processed: ticket voided and reissued. The next sync confirms the local records.");
                    return RedirectToAction(nameof(Index));

                case "retry":
                    await service.RetryReissueAsync(id, user.Id, adminNotes, CancellationToken.None);
                    SetSuccess("Reissue retried: the replacement ticket was issued to the receiver.");
                    return RedirectToAction(nameof(Index));

                case "marksuccessful":
                    await service.ApproveAsync(id, user.Id, adminNotes, ct);
                    SetSuccess("Transfer marked successful.");
                    return RedirectToAction(nameof(Index));

                case "cancel":
                    await service.RejectAsync(id, user.Id, adminNotes ?? string.Empty, ct);
                    SetSuccess("Transfer cancelled.");
                    return RedirectToAction(nameof(Index));

                default:
                    SetError("Unknown transfer action.");
                    return RedirectToAction(nameof(Detail), new { id });
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Decide rejected for transfer {TransferId} (action={Action}): {Message}",
                id, action, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Detail), new { id });
        }
    }
}

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

        IReadOnlyList<TicketTransferRowDto> rows = string.Equals(tab, "all", StringComparison.Ordinal)
            ? await BuildAllAsync(ct)
            : pending;

        var drift = (await ticketQueryService.GetOrderDriftAsync(ct))
            .OrderByDescending(r => r.IssuedCount - r.ValidCount)
            .ToList();

        return View(new TicketTransferIndexViewModel(
            ActiveTab: tab,
            PendingCount: pending.Count,
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
            if (approve)
            {
                await service.ApproveAsync(id, user.Id, adminNotes, ct);
                SetSuccess("Transfer marked successful.");
                return RedirectToAction(nameof(Index));
            }

            await service.RejectAsync(id, user.Id, adminNotes ?? string.Empty, ct);
            SetSuccess("Transfer cancelled.");
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Decide rejected for transfer {TransferId} (approve={Approve}): {Message}",
                id, approve, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Detail), new { id });
        }
    }
}

using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Transfers")]
public sealed class TicketTransferAdminController : HumansControllerBase
{
    private readonly ITicketTransferService _service;
    private readonly ILogger<TicketTransferAdminController> _logger;

    public TicketTransferAdminController(
        ITicketTransferService service,
        UserManager<User> userManager,
        ILogger<TicketTransferAdminController> logger)
        : base(userManager)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var pending = await _service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        return View(pending);
    }

    [HttpGet("Detail/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var rows = await _service.GetByStatusAsync(TicketTransferStatus.Pending, ct);
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return NotFound();
        }
        return View(row);
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
            await DispatchDecisionAsync(id, approve, user.Id, adminNotes, ct);
            SetSuccess(approve ? "Transfer approved." : "Transfer rejected.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Ticket transfer Decide rejected for transfer {TransferId} (approve={Approve}): {Message}",
                id, approve, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }

    private Task DispatchDecisionAsync(Guid id, bool approve, Guid userId, string? notes, CancellationToken ct) =>
        approve
            ? _service.ApproveAsync(id, userId, notes, ct)
            : _service.RejectAsync(id, userId, notes, ct);
}

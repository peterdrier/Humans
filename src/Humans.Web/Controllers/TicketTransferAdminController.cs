using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Transfers")]
public sealed class TicketTransferAdminController : HumansControllerBase
{
    private readonly ITicketTransferService _service;

    public TicketTransferAdminController(
        ITicketTransferService service,
        UserManager<User> userManager)
        : base(userManager)
    {
        _service = service;
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
            if (approve)
                await _service.ApproveAsync(id, user.Id, adminNotes, ct);
            else
                await _service.RejectAsync(id, user.Id, adminNotes, ct);
            SetSuccess(approve ? "Transfer approved." : "Transfer rejected.");
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

/// <summary>
/// Admin-only CRUD over the Consent Hold List. The hold list is consulted by
/// <see cref="Humans.Infrastructure.Jobs.AutoConsentCheckJob"/>: incoming legal
/// names are checked against these entries and any match blocks the LLM's
/// auto-approval. See docs/features/auto-consent-check.md.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Admin/ConsentHoldList")]
public class AdminConsentHoldListController : HumansControllerBase
{
    private readonly IConsentHoldListService _holdListService;
    private readonly ILogger<AdminConsentHoldListController> _logger;

    public AdminConsentHoldListController(
        UserManager<User> userManager,
        IConsentHoldListService holdListService,
        ILogger<AdminConsentHoldListController> logger)
        : base(userManager)
    {
        _holdListService = holdListService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var entries = await _holdListService.ListAsync();
        var viewModel = new ConsentHoldListViewModel
        {
            Entries = entries.Select(e => new ConsentHoldListEntryRow
            {
                Id = e.Id,
                Entry = e.Entry,
                Note = e.Note,
                AddedAt = e.AddedAt.ToDateTimeUtc(),
                AddedByUserId = e.AddedByUserId,
            }).ToList(),
        };
        return View(viewModel);
    }

    [HttpPost("Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string entry, string? note)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(entry))
        {
            SetError("Entry text is required.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _holdListService.AddAsync(entry, note, user.Id);
            SetSuccess("Hold list entry added.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add hold-list entry by admin {AdminId}", user.Id);
            SetError($"Failed to add entry: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error is not null) return error;

        try
        {
            await _holdListService.DeleteAsync(id, user.Id);
            SetSuccess("Hold list entry removed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete hold-list entry {Id} by admin {AdminId}", id, user.Id);
            SetError($"Failed to remove entry: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }
}

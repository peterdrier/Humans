using Humans.Application.Interfaces;
using Humans.Web.Authorization;
using Humans.Web.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Admin/Agent")]
public class AdminAgentController : Controller
{
    private readonly IAgentSettingsService _settings;
    private readonly IAgentService _agent;

    public AdminAgentController(IAgentSettingsService settings, IAgentService agent)
    {
        _settings = settings;
        _agent = agent;
    }

    [HttpGet("Settings")]
    public IActionResult Settings()
    {
        var s = _settings.Current;
        return View("~/Views/Admin/Agent/Settings.cshtml", new AdminAgentSettingsViewModel
        {
            Enabled = s.Enabled,
            Model = s.Model,
            PreloadConfig = s.PreloadConfig,
            DailyMessageCap = s.DailyMessageCap,
            HourlyMessageCap = s.HourlyMessageCap,
            DailyTokenCap = s.DailyTokenCap,
            RetentionDays = s.RetentionDays
        });
    }

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(AdminAgentSettingsViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View("~/Views/Admin/Agent/Settings.cshtml", vm);
        }

        await _settings.UpdateAsync(s =>
        {
            s.Enabled = vm.Enabled;
            s.Model = vm.Model;
            s.PreloadConfig = vm.PreloadConfig;
            s.DailyMessageCap = vm.DailyMessageCap;
            s.HourlyMessageCap = vm.HourlyMessageCap;
            s.DailyTokenCap = vm.DailyTokenCap;
            s.RetentionDays = vm.RetentionDays;
        }, ct);
        TempData["Status"] = "Settings saved.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("Conversations")]
    public async Task<IActionResult> Conversations(
        [FromServices] Humans.Application.Interfaces.Repositories.IAgentConversationRepository repo,
        bool refusalsOnly = false, bool handoffsOnly = false, Guid? userId = null,
        int page = 0, CancellationToken ct = default)
    {
        const int pageSize = 25;
        var rows = await repo.ListAllAsync(refusalsOnly, handoffsOnly, userId, pageSize, page * pageSize, ct);
        return View("~/Views/Admin/Agent/Conversations.cshtml", rows);
    }

    [HttpGet("Conversations/{id:guid}")]
    public async Task<IActionResult> ConversationDetail(Guid id,
        [FromServices] Humans.Application.Interfaces.Repositories.IAgentConversationRepository repo,
        CancellationToken ct)
    {
        var conv = await repo.GetByIdAsync(id, ct);
        if (conv is null) return NotFound();
        return View("~/Views/Admin/Agent/ConversationDetail.cshtml", conv);
    }
}

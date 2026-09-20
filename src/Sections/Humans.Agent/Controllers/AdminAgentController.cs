using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Agent.Services;
using Humans.Users.Contracts;

namespace Humans.Agent.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Agent/Admin")]
internal sealed class AdminAgentController(
    IAgentSettingsService settings,
    IAgentService agent,
    IAgentAdminStatusService status,
    IAgentPreloadCorpusBuilder preload,
    IUserServiceRead userService) : HumansControllerBase(userService)
{
    /// <summary>Index lands on Status — the operational view is the default
    /// destination for an admin clicking through the nav.</summary>
    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Status));

    [HttpGet("Status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var report = await status.GetStatusAsync(ct);
        var vm = new AdminAgentStatusViewModel(report, settings.Current);
        return View("~/Views/Admin/Agent/Status.cshtml", vm);
    }

    /// <summary>
    /// Superseded by the /Settings#agent tab (peterdrier/Humans#1634) — one canonical
    /// URL per page (memory/product/no-url-aliases.md), so a GET here always redirects
    /// there rather than staying a second live page.
    /// </summary>
    [HttpGet("Settings")]
    public IActionResult Settings() => Redirect("/Settings#agent");

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(AdminAgentSettingsViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View("~/Views/Admin/Agent/Settings.cshtml", vm);
        }

        await settings.UpdateAsync(s =>
        {
            s.Enabled = vm.Enabled;
            s.Model = vm.Model;
            s.PreloadConfig = vm.PreloadConfig;
            s.DailyMessageCap = vm.DailyMessageCap;
            s.HourlyMessageCap = vm.HourlyMessageCap;
            s.DailyTokenCap = vm.DailyTokenCap;
            s.RetentionDays = vm.RetentionDays;
        }, ct);
        SetSuccess("Settings saved.");
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost("ReloadKnowledgeBase")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReloadKnowledgeBase(CancellationToken ct)
    {
        await preload.ReloadAllAsync(ct);
        SetSuccess("Knowledge base reloaded from GitHub.");
        return RedirectToAction(nameof(Status));
    }

    [HttpGet("Conversations/{id:guid}/Prompt")]
    public async Task<IActionResult> ConversationPrompt(Guid id, CancellationToken ct)
    {
        var preview = await agent.GetPromptPreviewForAdminAsync(id, ct);
        if (preview is null) return NotFound();
        return View("~/Views/Admin/Agent/ConversationPrompt.cshtml", preview);
    }
}

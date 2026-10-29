using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Agent.Services;
using Humans.Users.Contracts;

namespace Humans.Agent.Controllers;

/// <summary>Board reads status and transcripts; only Admin changes settings or reloads the knowledge base.</summary>
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Agent/Admin")]
internal sealed class AdminAgentController(
    IAgentSettingsService settings,
    IAgentService agent,
    IAgentAdminStatusService status,
    IAgentPreloadCorpusBuilder preload,
    IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Status));

    [HttpGet("Status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var report = await status.GetStatusAsync(ct);
        var vm = new AdminAgentStatusViewModel(report, settings.Current);
        return View("~/Views/Admin/Agent/Status.cshtml", vm);
    }

    [HttpPost("Settings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(AdminAgentSettingsViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            vm.ModelOptions = await settings.GetModelOptionsAsync(ct);
            return View("~/Views/Admin/Agent/Settings.cshtml", vm);
        }

        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null) return Unauthorized();

        await settings.UpdateAsync(s =>
        {
            s.Enabled = vm.Enabled;
            s.Model = vm.Model;
            s.PreloadConfig = vm.PreloadConfig;
            s.DailyMessageCap = vm.DailyMessageCap;
            s.HourlyMessageCap = vm.HourlyMessageCap;
            s.DailyTokenCap = vm.DailyTokenCap;
            s.RetentionDays = vm.RetentionDays;
        }, actor.Id, ct);
        SetSuccess("Settings saved.");
        return Redirect("/Settings#agent");
    }

    [HttpPost("ReloadKnowledgeBase")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReloadKnowledgeBase(CancellationToken ct)
    {
        if (await preload.ReloadAllAsync(ct))
            SetSuccess("Knowledge base reloaded from GitHub.");
        else
            SetError("Knowledge base reload failed. The previous cached preload corpus was kept.");
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

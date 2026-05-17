using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("Agent/Admin")]
public class AdminAgentController : HumansControllerBase
{
    private readonly IAgentSettingsService _settings;
    private readonly IAgentService _agent;
    private readonly IAgentAdminStatusService _status;
    private readonly IUserService _users;

    public AdminAgentController(
        IAgentSettingsService settings,
        IAgentService agent,
        IAgentAdminStatusService status,
        IUserService users,
        IUserService userService)
        : base(userService)
    {
        _settings = settings;
        _agent = agent;
        _status = status;
        _users = users;
    }

    /// <summary>Index lands on Status — the operational view is the default
    /// destination for an admin clicking through the nav.</summary>
    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Status));

    [HttpGet("Status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var report = await _status.GetStatusAsync(ct);
        var vm = new AdminAgentStatusViewModel(report, _settings.Current);
        return View("~/Views/Admin/Agent/Status.cshtml", vm);
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
        SetSuccess("Settings saved.");
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("Conversations/{id:guid}/Prompt")]
    public async Task<IActionResult> ConversationPrompt(Guid id, CancellationToken ct)
    {
        var preview = await _agent.GetPromptPreviewForAdminAsync(id, ct);
        if (preview is null) return NotFound();
        return View("~/Views/Admin/Agent/ConversationPrompt.cshtml", preview);
    }
}

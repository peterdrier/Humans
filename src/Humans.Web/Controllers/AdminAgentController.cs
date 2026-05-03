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
    private readonly IUserService _users;

    public AdminAgentController(
        IAgentSettingsService settings,
        IAgentService agent,
        IUserService users,
        UserManager<User> userManager)
        : base(userManager)
    {
        _settings = settings;
        _agent = agent;
        _users = users;
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

    [HttpGet("Conversations")]
    public async Task<IActionResult> Conversations(
        bool refusalsOnly = false, bool handoffsOnly = false, Guid? userId = null,
        int page = 0, CancellationToken ct = default)
    {
        const int pageSize = 25;
        var rows = await _agent.ListAllConversationsForAdminAsync(
            refusalsOnly, handoffsOnly, userId, pageSize, page * pageSize, ct);

        // Stitch display names so the view can render <human-link> with names
        // (cross-domain join lives in the service/controller, not the entity).
        var distinctUserIds = rows.Select(r => r.UserId).Distinct().ToArray();
        IReadOnlyDictionary<Guid, User> users = distinctUserIds.Length == 0
            ? new Dictionary<Guid, User>()
            : await _users.GetByIdsAsync(distinctUserIds, ct);
        var vm = rows.Select(r => new AdminAgentConversationRow(
            Conversation: r,
            DisplayName: users.TryGetValue(r.UserId, out var u) ? u.DisplayName : r.UserId.ToString())
        ).ToList();

        return View("~/Views/Admin/Agent/Conversations.cshtml", vm);
    }

    [HttpGet("Conversations/{id:guid}")]
    public async Task<IActionResult> ConversationDetail(Guid id, CancellationToken ct)
    {
        var conv = await _agent.GetConversationForAdminAsync(id, ct);
        if (conv is null) return NotFound();
        var user = await _users.GetByIdAsync(conv.UserId, ct);
        var vm = new AdminAgentConversationDetail(
            Conversation: conv,
            DisplayName: user?.DisplayName ?? conv.UserId.ToString());
        return View("~/Views/Admin/Agent/ConversationDetail.cshtml", vm);
    }

    [HttpGet("Conversations/{id:guid}/Prompt")]
    public async Task<IActionResult> ConversationPrompt(Guid id, CancellationToken ct)
    {
        var preview = await _agent.GetPromptPreviewForAdminAsync(id, ct);
        if (preview is null) return NotFound();
        return View("~/Views/Admin/Agent/ConversationPrompt.cshtml", preview);
    }
}

/// <summary>Conversations list row stitched with display name (cross-domain join via service).</summary>
public sealed record AdminAgentConversationRow(AgentConversation Conversation, string DisplayName);

/// <summary>Conversation detail with display name resolved.</summary>
public sealed record AdminAgentConversationDetail(AgentConversation Conversation, string DisplayName);

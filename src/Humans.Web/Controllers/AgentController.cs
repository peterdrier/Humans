using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Humans.Application.Models;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Humans.Web.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Agent")]
public class AgentController : HumansControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    }.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);

    private readonly IAgentService _agent;
    private readonly IAuthorizationService _auth;
    private readonly IAgentSettingsService _settings;
    private readonly IUserService _users;

    public AgentController(
        IAgentService agent,
        IAuthorizationService auth,
        IAgentSettingsService settings,
        IUserService users,
        IUserService userService)
        : base(userService)
    {
        _agent = agent;
        _auth = auth;
        _settings = settings;
        _users = users;
    }

    [HttpPost("Ask")]
    [ValidateAntiForgeryToken]
    public async Task Ask([FromBody] AgentAskRequest body, CancellationToken cancellationToken)
    {
        var (missing, user) = await RequireCurrentUserAsync();
        if (missing is not null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!_settings.Current.Enabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var rate = await _auth.AuthorizeAsync(User, user.Id, PolicyNames.AgentRateLimit);
        if (!rate.Succeeded)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        await Response.Body.FlushAsync(cancellationToken);

        var req = new AgentTurnRequest(
            ConversationId: body.ConversationId ?? Guid.Empty,
            UserId: user.Id,
            Message: body.Message,
            Locale: user.PreferredLanguage);

        await foreach (var token in _agent.AskAsync(req, cancellationToken))
        {
            await WriteSse(token, cancellationToken);
        }
    }

    [HttpGet("Conversations")]
    public async Task<IActionResult> Conversations(
        bool refusalsOnly = false, Guid? userId = null,
        int page = 0, CancellationToken cancellationToken = default)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        var isAdmin = User.IsInRole(RoleNames.Admin);
        var rows = await LoadConversationsAsync(
            isAdmin, currentUser.Id, refusalsOnly, userId, page, cancellationToken);
        var listRows = await StitchListRowsAsync(rows, isAdmin, cancellationToken);
        return View(new AgentConversationsViewModel(listRows, IsAdminView: isAdmin));
    }

    [HttpGet("Conversation/{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken cancellationToken)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        // Ownership mismatch returns 404 (not 403) per Agent.md invariant 7
        // and the issue #632 spec — the existence of someone else's
        // conversation must not be inferable from the response code.
        var view = await _agent.GetMyConversationAsync(currentUser.Id, id, cancellationToken);
        if (view is null) return NotFound();

        return View(new AgentMyConversationViewModel(view.Conversation, view.CurrentUserContextTail));
    }

    [HttpGet("Conversations/{id:guid}")]
    public async Task<IActionResult> ConversationDetail(Guid id, CancellationToken cancellationToken)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        var isAdmin = User.IsInRole(RoleNames.Admin);
        var conv = await LoadConversationForViewerAsync(isAdmin, currentUser.Id, id, cancellationToken);
        if (conv is null) return NotFound();

        var displayName = await ResolveOwnerDisplayNameAsync(isAdmin, conv, cancellationToken);
        return View(new AgentConversationDetailViewModel(conv, displayName, IsAdminView: isAdmin));
    }

    private async Task<IReadOnlyList<AgentConversationListSnapshot>> LoadConversationsAsync(
        bool isAdmin, Guid currentUserId,
        bool refusalsOnly, Guid? userId, int page,
        CancellationToken ct)
    {
        const int adminPageSize = 25;
        // Clamp negative page values from the URL — a negative offset would
        // otherwise crash the EF paging call with a 500.
        var safePage = page < 0 ? 0 : page;
        if (!isAdmin)
            return await _agent.GetHistoryAsync(currentUserId, take: 50, ct);

        return await _agent.ListAllConversationsForAdminAsync(
            refusalsOnly, userId, adminPageSize, safePage * adminPageSize, ct);
    }

    private async Task<List<AgentConversationRow>> StitchListRowsAsync(
        IReadOnlyList<AgentConversationListSnapshot> rows, bool isAdmin, CancellationToken ct)
    {
        // Display names are only meaningful in admin mode (where the table
        // shows the Human column). Skip the lookup entirely for non-admins.
        if (!isAdmin || rows.Count == 0)
            return rows.Select(r => new AgentConversationRow(r, DisplayName: null)).ToList();

        var distinctUserIds = rows.Select(r => r.UserId).Distinct().ToArray();
        var users = await _users.GetUserInfosAsync(distinctUserIds, ct);
        return rows.Select(r => new AgentConversationRow(
            Conversation: r,
            DisplayName: users.TryGetValue(r.UserId, out var u) ? u.BurnerName : null)
        ).ToList();
    }

    private async Task<AgentConversationTranscriptSnapshot?> LoadConversationForViewerAsync(
        bool isAdmin, Guid currentUserId, Guid conversationId, CancellationToken ct)
    {
        if (isAdmin)
            return await _agent.GetConversationForAdminAsync(conversationId, ct);

        return await _agent.GetConversationForUserAsync(currentUserId, conversationId, ct);
    }

    private async Task<string?> ResolveOwnerDisplayNameAsync(
        bool isAdmin, AgentConversationTranscriptSnapshot conv, CancellationToken ct)
    {
        if (!isAdmin) return null;
        var owner = await _users.GetUserInfoAsync(conv.UserId, ct);
        return owner?.BurnerName ?? conv.UserId.ToString();
    }

    private async Task WriteSse(AgentTurnToken token, CancellationToken cancellationToken)
    {
        string eventName = token.TextDelta is not null ? "text"
                         : token.ToolCall is not null ? "tool"
                         : token.IssueProposal is not null ? "propose"
                         : "final";
        var payload = JsonSerializer.Serialize(token, JsonOpts);
        await Response.WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"event: {eventName}\ndata: {payload}\n\n"),
            cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}

/// <summary>List of conversations + an admin flag the view uses to decide whether
/// to surface admin-only chrome (Human column, refusal filters).</summary>
public sealed record AgentConversationsViewModel(
    IReadOnlyList<AgentConversationRow> Rows,
    bool IsAdminView);

/// <summary>One row in the conversations list. <see cref="DisplayName"/> is null
/// for non-admin views (the column is hidden) and stitched in from
/// <c>IUserService</c> for admin views.</summary>
public sealed record AgentConversationRow(AgentConversationListSnapshot Conversation, string? DisplayName);

/// <summary>Conversation detail with display name (admin only) and an admin flag
/// the view uses to gate token counts, tool invocations, and the prompt-preview
/// link.</summary>
public sealed record AgentConversationDetailViewModel(
    AgentConversationTranscriptSnapshot Conversation,
    string? DisplayName,
    bool IsAdminView);

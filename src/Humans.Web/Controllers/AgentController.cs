using System.Globalization;
using System.Text.Json;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Users;
using Humans.Application.Models;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Serialization.SystemTextJson;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Agent")]
public class AgentController : HumansControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);

    private readonly IAgentService _agent;
    private readonly IAuthorizationService _auth;
    private readonly IConsentService _consents;
    private readonly IAgentSettingsService _settings;
    private readonly IUserService _users;

    public AgentController(
        IAgentService agent,
        IAuthorizationService auth,
        IConsentService consents,
        IAgentSettingsService settings,
        IUserService users,
        UserManager<User> userManager)
        : base(userManager)
    {
        _agent = agent;
        _auth = auth;
        _consents = consents;
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

        var pendingDocs = await _consents.GetPendingDocumentNamesAsync(user.Id, cancellationToken);
        if (pendingDocs.Contains(LegalDocumentNames.AgentChatTerms, StringComparer.Ordinal))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
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
        bool refusalsOnly = false, bool handoffsOnly = false, Guid? userId = null,
        int page = 0, CancellationToken cancellationToken = default)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        var isAdmin = User.IsInRole(RoleNames.Admin);
        IReadOnlyList<AgentConversation> rows;

        if (isAdmin)
        {
            const int pageSize = 25;
            rows = await _agent.ListAllConversationsForAdminAsync(
                refusalsOnly, handoffsOnly, userId, pageSize, page * pageSize, cancellationToken);
        }
        else
        {
            // Non-admins see only their own. Admin filters are ignored.
            rows = await _agent.GetHistoryAsync(currentUser.Id, take: 50, cancellationToken);
        }

        // Display names are only meaningful for admins (who see other people's
        // conversations). Stitch them in admin mode; for users the column is
        // hidden entirely.
        IReadOnlyDictionary<Guid, User>? users = null;
        if (isAdmin && rows.Count > 0)
        {
            var distinctUserIds = rows.Select(r => r.UserId).Distinct().ToArray();
            users = await _users.GetByIdsAsync(distinctUserIds, cancellationToken);
        }

        var listRows = rows.Select(r => new AgentConversationRow(
            Conversation: r,
            DisplayName: users is not null && users.TryGetValue(r.UserId, out var u)
                ? u.DisplayName
                : null)
        ).ToList();

        return View(new AgentConversationsViewModel(listRows, IsAdminView: isAdmin));
    }

    [HttpGet("Conversations/{id:guid}")]
    public async Task<IActionResult> ConversationDetail(Guid id, CancellationToken cancellationToken)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        var isAdmin = User.IsInRole(RoleNames.Admin);

        var conv = isAdmin
            ? await _agent.GetConversationForAdminAsync(id, cancellationToken)
            : await _agent.GetConversationForUserAsync(currentUser.Id, id, cancellationToken);
        if (conv is null) return NotFound();

        string? displayName = null;
        if (isAdmin)
        {
            var owner = await _users.GetByIdAsync(conv.UserId, cancellationToken);
            displayName = owner?.DisplayName ?? conv.UserId.ToString();
        }

        return View(new AgentConversationDetailViewModel(conv, displayName, IsAdminView: isAdmin));
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
/// to surface admin-only chrome (Human column, refusal/handoff filters).</summary>
public sealed record AgentConversationsViewModel(
    IReadOnlyList<AgentConversationRow> Rows,
    bool IsAdminView);

/// <summary>One row in the conversations list. <see cref="DisplayName"/> is null
/// for non-admin views (the column is hidden) and stitched in from
/// <c>IUserService</c> for admin views.</summary>
public sealed record AgentConversationRow(AgentConversation Conversation, string? DisplayName);

/// <summary>Conversation detail with display name (admin only) and an admin flag
/// the view uses to gate token counts, tool invocations, and the prompt-preview
/// link.</summary>
public sealed record AgentConversationDetailViewModel(
    AgentConversation Conversation,
    string? DisplayName,
    bool IsAdminView);

using Humans.Agent.Contracts;
using Humans.Agent.Authorization;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Constants;
using Humans.Agent.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime.Serialization.SystemTextJson;
using Humans.Agent.Services;
using Humans.Users.Contracts;

namespace Humans.Agent.Controllers;

[Authorize]
[Route("Agent")]
internal sealed class AgentController(
    IAgentService agent,
    IAuthorizationService auth,
    IAgentSettingsService settings,
    IUserService users) : HumansControllerBase(users)
{
    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    }.ConfigureForNodaTime(NodaTime.DateTimeZoneProviders.Tzdb);

    [HttpPost("Ask")]
    [ValidateAntiForgeryToken]
    public async Task Ask([FromBody] AgentAskRequest body, CancellationToken cancellationToken)
    {
        var (missing, user) = await RequireCurrentUserAsync(cancellationToken);
        if (missing is not null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Pattern match also rejects a null Message: STJ binds {"message":null} despite the
        // non-nullable declaration, and a null body binds when the raw payload is `null`.
        if (body?.Message is not { Length: <= AgentAskRequest.MaxMessageLength })
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!settings.Current.Enabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // The requirement is passed directly, not through a named policy: it and its handler are internal to the
        // section, and this is the only caller.
        var rate = await auth.AuthorizeAsync(User, user.Id, [new AgentRateLimitRequirement()]);
        if (!rate.Succeeded)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var req = new AgentTurnRequest(
            ConversationId: body.ConversationId ?? Guid.Empty,
            UserId: user.Id,
            Message: body.Message,
            Locale: user.PreferredLanguage);

        // Pull the first token before committing the 200: the service checks conversation
        // ownership on that first step, and once the stream is flushed the status is fixed.
        await using var tokens = agent.AskAsync(req, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool more;
        try
        {
            more = await tokens.MoveNextAsync();
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        await Response.Body.FlushAsync(cancellationToken);

        for (; more; more = await tokens.MoveNextAsync())
            await WriteSse(tokens.Current, cancellationToken);
    }

    [HttpGet("Conversations")]
    public async Task<IActionResult> Conversations(
        bool refusalsOnly = false, Guid? userId = null,
        int page = 0, CancellationToken cancellationToken = default)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync(cancellationToken);
        if (missing is not null) return missing;

        var isAdmin = RoleChecks.IsAdminOrBoard(User);
        var (rows, hasNext) = await LoadConversationsAsync(
            isAdmin, currentUser.Id, refusalsOnly, userId, page, cancellationToken);
        return View(new AgentConversationsViewModel(
            rows, IsAdminView: isAdmin, Page: page < 0 ? 0 : page, HasNext: hasNext));
    }

    [HttpGet("Conversation/{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken cancellationToken)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync(cancellationToken);
        if (missing is not null) return missing;

        // 404, not 403: someone else's conversation must not be inferable from the status code.
        var view = await agent.GetMyConversationAsync(currentUser.Id, id, cancellationToken);
        if (view is null) return NotFound();

        return View(view);
    }

    [HttpGet("Conversations/{id:guid}")]
    public async Task<IActionResult> ConversationDetail(Guid id, CancellationToken cancellationToken)
    {
        var (missing, currentUser) = await RequireCurrentUserAsync(cancellationToken);
        if (missing is not null) return missing;

        var isAdmin = RoleChecks.IsAdminOrBoard(User);
        var conv = await LoadConversationForViewerAsync(isAdmin, currentUser.Id, id, cancellationToken);
        if (conv is null) return NotFound();

        return View(new AgentConversationDetailViewModel(conv, IsAdminView: isAdmin));
    }

    private async Task<(IReadOnlyList<AgentConversationListSnapshot> Rows, bool HasNext)> LoadConversationsAsync(
        bool isAdmin, Guid currentUserId,
        bool refusalsOnly, Guid? userId, int page,
        CancellationToken ct)
    {
        const int adminPageSize = 25;
        // Clamp negative page values from the URL — a negative offset would
        // otherwise crash the EF paging call with a 500.
        var safePage = page < 0 ? 0 : page;
        if (!isAdmin)
            return (await agent.GetHistoryAsync(currentUserId, take: 50, ct), false);

        // Saturate large offsets so a valid page number cannot wrap into an earlier window.
        var skip = (int)Math.Min((long)safePage * adminPageSize, int.MaxValue);
        // Fetch one extra row so the view knows whether an older page exists.
        var rows = await agent.ListAllConversationsForAdminAsync(
            refusalsOnly, userId, adminPageSize + 1, skip, ct);
        return rows.Count > adminPageSize
            ? (rows.Take(adminPageSize).ToList(), true)
            : (rows, false);
    }

    private async Task<AgentConversationTranscriptSnapshot?> LoadConversationForViewerAsync(
        bool isAdmin, Guid currentUserId, Guid conversationId, CancellationToken ct)
    {
        if (isAdmin)
            return await agent.GetConversationForAdminAsync(conversationId, ct);

        return await agent.GetConversationForUserAsync(currentUserId, conversationId, ct);
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

/// <summary>Paging applies to the admin view only. <see cref="Page"/> is zero-based; <see cref="HasNext"/> means an
/// older page exists.</summary>
internal sealed record AgentConversationsViewModel(
    IReadOnlyList<AgentConversationListSnapshot> Rows,
    bool IsAdminView,
    int Page = 0,
    bool HasNext = false);

internal sealed record AgentConversationDetailViewModel(
    AgentConversationTranscriptSnapshot Conversation,
    bool IsAdminView);

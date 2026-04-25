using System.Globalization;
using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Models;
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

    public AgentController(
        IAgentService agent,
        IAuthorizationService auth,
        IConsentService consents,
        IAgentSettingsService settings,
        UserManager<User> userManager)
        : base(userManager)
    {
        _agent = agent;
        _auth = auth;
        _consents = consents;
        _settings = settings;
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
        if (pendingDocs.Contains("Agent Chat Terms", StringComparer.OrdinalIgnoreCase))
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

    [HttpGet("History")]
    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        var (missing, user) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        var history = await _agent.GetHistoryAsync(user.Id, take: 50, cancellationToken);
        return View(history);
    }

    [HttpDelete("Conversation/{id:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken cancellationToken)
    {
        var (missing, user) = await RequireCurrentUserAsync();
        if (missing is not null) return missing;

        await _agent.DeleteConversationAsync(user.Id, id, cancellationToken);
        return NoContent();
    }

    private async Task WriteSse(AgentTurnToken token, CancellationToken cancellationToken)
    {
        string eventName = token.TextDelta is not null ? "text"
                         : token.ToolCall is not null ? "tool"
                         : "final";
        var payload = JsonSerializer.Serialize(token, JsonOpts);
        await Response.WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"event: {eventName}\ndata: {payload}\n\n"),
            cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}

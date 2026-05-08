using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Read-only API surface for QA/prod chat-history review (issue #631). Gated by
/// <see cref="AgentApiKeyAuthFilter"/> so dev tooling and a dev-side Claude can
/// pull recent agent conversations to look for refusal/handoff patterns and
/// prompt-tuning opportunities. No mutations live here — deletion and settings
/// remain on the admin web UI.
/// </summary>
[ApiController]
[Route("api/agent")]
[ServiceFilter(typeof(AgentApiKeyAuthFilter))]
public class AgentApiController : ControllerBase
{
    private readonly IAgentService _agent;
    private readonly IUserService _users;

    public AgentApiController(IAgentService agent, IUserService users)
    {
        _agent = agent;
        _users = users;
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> List(
        [FromQuery] bool refusalsOnly = false,
        [FromQuery] bool handoffsOnly = false,
        [FromQuery] Guid? userId = null,
        [FromQuery] int take = 50,
        [FromQuery] int skip = 0,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        if (skip < 0) skip = 0;

        var rows = await _agent.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly, handoffsOnly, userId, take, skip, ct);
        var users = await ResolveUsersAsync(rows.Select(c => c.UserId), ct);
        return Ok(rows.Select(r => ToSummary(r, users)));
    }

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var conv = await _agent.GetConversationForAdminAsync(id, ct);
        if (conv is null) return NotFound();

        var users = await ResolveUsersAsync(new[] { conv.UserId }, ct);
        var displayName = users.TryGetValue(conv.UserId, out var u) ? u.DisplayName : null;

        return Ok(new
        {
            conv.Id,
            conv.UserId,
            UserDisplayName = displayName,
            conv.Locale,
            StartedAt = conv.StartedAt.ToDateTimeUtc(),
            LastMessageAt = conv.LastMessageAt.ToDateTimeUtc(),
            conv.MessageCount,
            RefusalCount = conv.Messages.Count(m => m.RefusalReason is not null),
            HandoffCount = conv.Messages.Count(m => m.HandedOffToFeedbackId is not null),
            Messages = conv.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto)
        });
    }

    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid id, CancellationToken ct)
    {
        var conv = await _agent.GetConversationForAdminAsync(id, ct);
        if (conv is null) return NotFound();

        return Ok(conv.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto));
    }

    private async Task<IReadOnlyDictionary<Guid, User>> ResolveUsersAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
            return new Dictionary<Guid, User>();
        return await _users.GetByIdsAsync(distinct, ct);
    }

    private static object ToSummary(AgentConversation c, IReadOnlyDictionary<Guid, User> users)
    {
        // Most-recent user message preview is useful at-a-glance triage signal —
        // matches the listing UX in the admin web view but stays JSON-clean for
        // the API consumer.
#pragma warning disable CS0618 // AgentRole.User trips NoObsoleteNavReads ratchet false-positive (see design-rules §6c)
        var lastUserMessage = c.Messages
            .Where(m => m.Role == AgentRole.User && !string.IsNullOrEmpty(m.Content))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefault();
#pragma warning restore CS0618
        var preview = lastUserMessage?.Content;
        if (preview is { Length: > 200 }) preview = preview[..200];

        return new
        {
            c.Id,
            c.UserId,
            UserDisplayName = users.TryGetValue(c.UserId, out var u) ? u.DisplayName : null,
            c.Locale,
            StartedAt = c.StartedAt.ToDateTimeUtc(),
            LastMessageAt = c.LastMessageAt.ToDateTimeUtc(),
            c.MessageCount,
            RefusalCount = c.Messages.Count(m => m.RefusalReason is not null),
            HandoffCount = c.Messages.Count(m => m.HandedOffToFeedbackId is not null),
            LastUserMessagePreview = preview
        };
    }

    private static object ToMessageDto(AgentMessage m) => new
    {
        m.Id,
        Role = m.Role.ToString(),
        m.Content,
        CreatedAt = m.CreatedAt.ToDateTimeUtc(),
        m.Model,
        m.RefusalReason,
        m.HandedOffToFeedbackId,
        FetchedDocs = m.FetchedDocs
    };
}

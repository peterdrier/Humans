using Humans.Agent.Contracts;
using Humans.Backdoor.Filters;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Read-only chat-history review for QA/prod. Mutations stay on the admin web UI.
/// </summary>
[ApiController]
[Route("api/backdoor/agent")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
internal sealed class BackdoorAgentController(IAgentTranscriptRead agent, IUserServiceRead users)
    : ControllerBase
{
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

        var rows = await agent.ListAllConversationsForAdminWithMessagesAsync(
            refusalsOnly, handoffsOnly, userId, take, skip, ct);
        var resolved = await ResolveUsersAsync(rows.Select(c => c.UserId), ct);
        return Ok(rows.Select(r => ToSummary(r, resolved)));
    }

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var conv = await agent.GetConversationForAdminAsync(id, ct);
        if (conv is null) return NotFound();

        var resolved = await ResolveUsersAsync([conv.UserId], ct);
        var displayName = resolved.TryGetValue(conv.UserId, out var u) ? u.BurnerName : null;

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
            HandoffCount = conv.Messages.Count(m => m.IsHandoff),
            Messages = conv.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto)
        });
    }

    [HttpGet("conversations/{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid id, CancellationToken ct)
    {
        var conv = await agent.GetConversationForAdminAsync(id, ct);
        if (conv is null) return NotFound();

        return Ok(conv.Messages.OrderBy(m => m.CreatedAt).Select(ToMessageDto));
    }

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> ResolveUsersAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
            return new Dictionary<Guid, UserInfo>();
        return await users.GetUserInfosAsync(distinct, ct);
    }

    private static object ToSummary(
        AgentConversationTranscriptSnapshot c, IReadOnlyDictionary<Guid, UserInfo> users)
    {
        var lastUserMessage = c.Messages
            .Where(m => m.Role == AgentRole.User && !string.IsNullOrEmpty(m.Content))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefault();
        var preview = lastUserMessage?.Content;
        if (preview is { Length: > 200 }) preview = preview[..200];

        return new
        {
            c.Id,
            c.UserId,
            UserDisplayName = users.TryGetValue(c.UserId, out var u) ? u.BurnerName : null,
            c.Locale,
            StartedAt = c.StartedAt.ToDateTimeUtc(),
            LastMessageAt = c.LastMessageAt.ToDateTimeUtc(),
            c.MessageCount,
            RefusalCount = c.Messages.Count(m => m.RefusalReason is not null),
            HandoffCount = c.Messages.Count(m => m.IsHandoff),
            LastUserMessagePreview = preview
        };
    }

    private static object ToMessageDto(AgentMessageSnapshot m) => new
    {
        m.Id,
        Role = m.Role.ToString(),
        m.Content,
        CreatedAt = m.CreatedAt.ToDateTimeUtc(),
        m.Model,
        m.RefusalReason,
        m.HandedOffToFeedbackId,
        m.FetchedDocs
    };
}

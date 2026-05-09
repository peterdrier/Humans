using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;
using Humans.Application.Interfaces.Feedback;

// FeedbackReport / FeedbackMessage cross-domain nav properties (User, ResolvedByUser,
// AssignedToUser, AssignedToTeam, SenderUser) are [Obsolete] — FeedbackService stitches
// them in memory from IUserService / ITeamService so controllers can continue to read
// them for response shaping. Nav-strip follow-up tracked in design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/feedback")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class FeedbackApiController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly IProfileService _profileService;
    private readonly ILogger<FeedbackApiController> _logger;

    public FeedbackApiController(
        IFeedbackService feedbackService,
        IProfileService profileService,
        ILogger<FeedbackApiController> logger)
    {
        _feedbackService = feedbackService;
        _profileService = profileService;
        _logger = logger;
    }

    /// <summary>
    /// Issue #692: BurnerName-aware display-name resolution for feedback API
    /// payloads. Profile save write-through-syncs <c>User.DisplayName</c> to
    /// <c>Profile.BurnerName</c>, so this is the canonical "what name does
    /// the human want shown" path.
    /// </summary>
    private static string ResolveName(
        Guid id,
        string fallback,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles) =>
        profiles.TryGetValue(id, out var p) && !string.IsNullOrWhiteSpace(p.BurnerName)
            ? p.BurnerName
            : fallback;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] FeedbackStatus? status,
        [FromQuery] FeedbackCategory? category,
        [FromQuery] int limit = 50)
    {
        var reports = await _feedbackService.GetFeedbackListAsync(status, category, limit: limit);

        var nameUserIds = reports
            .SelectMany(r => new[] { r.UserId, r.AssignedToUserId ?? Guid.Empty, r.ResolvedByUserId ?? Guid.Empty })
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var profiles = nameUserIds.Count > 0
            ? await _profileService.GetByUserIdsAsync(nameUserIds)
            : (IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile>)new Dictionary<Guid, Humans.Domain.Entities.Profile>();

        var result = reports.Select(r => new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = ResolveName(r.UserId, r.User.DisplayName, profiles),
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUserId.HasValue
                ? ResolveName(r.ResolvedByUserId.Value, r.ResolvedByUser?.DisplayName ?? string.Empty, profiles)
                : null,
            MessageCount = r.Messages.Count,
            AssignedToUserId = r.AssignedToUserId,
            AssignedToName = r.AssignedToUserId.HasValue
                ? ResolveName(r.AssignedToUserId.Value, r.AssignedToUser?.DisplayName ?? string.Empty, profiles)
                : null,
            AssignedToTeamId = r.AssignedToTeamId,
            AssignedToTeamName = r.AssignedToTeam?.Name
        });

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var r = await _feedbackService.GetFeedbackByIdAsync(id);
        if (r is null) return NotFound();

        var profiles = await LoadFeedbackProfilesAsync(r);
        return Ok(BuildFeedbackDetailPayload(r, profiles));
    }

    private async Task<IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile>> LoadFeedbackProfilesAsync(
        Humans.Domain.Entities.FeedbackReport r)
    {
        var userIds = new[]
            {
                r.UserId,
                r.AssignedToUserId ?? Guid.Empty,
                r.ResolvedByUserId ?? Guid.Empty
            }
            .Concat(r.Messages.Select(m => m.SenderUserId ?? Guid.Empty))
            .Where(uid => uid != Guid.Empty)
            .Distinct()
            .ToList();
        return userIds.Count > 0
            ? await _profileService.GetByUserIdsAsync(userIds)
            : new Dictionary<Guid, Humans.Domain.Entities.Profile>();
    }

    private static object BuildFeedbackDetailPayload(
        Humans.Domain.Entities.FeedbackReport r,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles) =>
        new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = ResolveName(r.UserId, r.User.DisplayName, profiles),
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = ResolveOptionalName(r.ResolvedByUserId, r.ResolvedByUser?.DisplayName, profiles),
            AssignedToUserId = r.AssignedToUserId,
            AssignedToName = ResolveOptionalName(r.AssignedToUserId, r.AssignedToUser?.DisplayName, profiles),
            AssignedToTeamId = r.AssignedToTeamId,
            AssignedToTeamName = r.AssignedToTeam?.Name,
            Messages = r.Messages.Select(m => MapMessage(m, profiles, r.UserId))
        };

    private static object MapMessage(
        Humans.Domain.Entities.FeedbackMessage m,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles,
        Guid reporterUserId) =>
        new
        {
            m.Id,
            SenderName = ResolveOptionalName(m.SenderUserId, m.SenderUser?.DisplayName, profiles) ?? "Unknown",
            m.SenderUserId,
            m.Content,
            CreatedAt = m.CreatedAt.ToDateTimeUtc(),
            IsReporter = m.SenderUserId.HasValue && m.SenderUserId == reporterUserId
        };

    private static string? ResolveOptionalName(
        Guid? userId,
        string? fallback,
        IReadOnlyDictionary<Guid, Humans.Domain.Entities.Profile> profiles) =>
        userId.HasValue ? ResolveName(userId.Value, fallback ?? string.Empty, profiles) : null;

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var messages = await _feedbackService.GetMessagesAsync(id);

        return Ok(messages.Select(m => new
        {
            m.Id,
            SenderName = m.SenderUser?.DisplayName ?? "Unknown",
            m.SenderUserId,
            m.Content,
            CreatedAt = m.CreatedAt.ToDateTimeUtc(),
            IsReporter = m.SenderUserId.HasValue && m.SenderUserId == report.UserId
        }));
    }

    [HttpPost("{id}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostFeedbackMessageModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var message = await _feedbackService.PostMessageAsync(id, null, model.Content, isAdmin: true);
            return Ok(new
            {
                message.Id,
                message.Content,
                CreatedAt = message.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to post message" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model)
    {
        try
        {
            await _feedbackService.UpdateStatusAsync(id, model.Status, null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/assignment")]
    public async Task<IActionResult> UpdateAssignment(Guid id, [FromBody] UpdateFeedbackAssignmentModel model)
    {
        try
        {
            await _feedbackService.UpdateAssignmentAsync(id, model.AssignedToUserId, model.AssignedToTeamId, null);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update assignment for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to update assignment" });
        }
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            return Ok(new { success = true });
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }
}

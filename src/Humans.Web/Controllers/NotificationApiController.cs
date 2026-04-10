using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Data;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/notifications")]
[ServiceFilter(typeof(NotificationApiKeyAuthFilter))]
public class NotificationApiController : ControllerBase
{
    private readonly INotificationInboxService _inboxService;
    private readonly INotificationMeterProvider _meterProvider;
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly NotificationApiSettings _settings;
    private readonly ILogger<NotificationApiController> _logger;

    public NotificationApiController(
        INotificationInboxService inboxService,
        INotificationMeterProvider meterProvider,
        HumansDbContext dbContext,
        IClock clock,
        IOptions<NotificationApiSettings> settings,
        ILogger<NotificationApiController> logger)
    {
        _inboxService = inboxService;
        _meterProvider = meterProvider;
        _dbContext = dbContext;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? since = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(_settings.UserId, out var userId))
        {
            _logger.LogError("NotificationApi:UserId is not configured or invalid");
            return StatusCode(503, new { error = "UserId not configured" });
        }

        try
        {
            // Build inbox query — unread tab, no filter, no search
            var inbox = await _inboxService.GetInboxAsync(userId, search: null, filter: "", tab: "unread", ct);

            var notifications = inbox.NeedsAttention
                .Concat(inbox.Informational)
                .AsEnumerable();

            // Apply since filter if provided
            if (since is not null)
            {
                var parseResult = LocalDatePattern.Iso.Parse(since);
                if (!parseResult.Success)
                {
                    return BadRequest(new { error = "Invalid 'since' format. Use yyyy-MM-dd." });
                }

                var sinceInstant = parseResult.Value.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
                var sinceUtc = sinceInstant.ToDateTimeUtc();
                notifications = notifications.Where(n => n.CreatedAt >= sinceUtc);
            }

            var notificationList = notifications.Select(n => new
            {
                Date = n.CreatedAt,
                Source = n.Source.ToString(),
                Subject = n.Title,
                Link = n.ActionUrl,
                Priority = n.Priority.ToString().ToLowerInvariant(),
                Class = n.Class.ToString().ToLowerInvariant(),
                Unread = !n.IsRead,
            }).ToList();

            // Build meters using a ClaimsPrincipal for the configured user
            var principal = await BuildClaimsPrincipalAsync(userId, ct);
            var meters = await _meterProvider.GetMetersForUserAsync(principal, ct);

            var meterList = meters.Select(m => new
            {
                Label = m.Title,
                m.Count,
                Link = m.ActionUrl,
            }).ToList();

            return Ok(new
            {
                Notifications = notificationList,
                Meters = meterList,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve notifications for API");
            return StatusCode(500, new { error = "Failed to retrieve notifications" });
        }
    }

    private async Task<ClaimsPrincipal> BuildClaimsPrincipalAsync(Guid userId, CancellationToken ct)
    {
        var now = _clock.GetCurrentInstant();

        var roles = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.UserId == userId &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.RoleName)
            .Distinct()
            .ToListAsync(ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "ApiKey");
        return new ClaimsPrincipal(identity);
    }
}

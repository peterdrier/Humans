using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class FeedbackService : IFeedbackService
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly ILogger<FeedbackService> _logger;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    private const long MaxScreenshotBytes = 10 * 1024 * 1024; // 10MB

    public FeedbackService(
        HumansDbContext dbContext,
        IEmailService emailService,
        IAuditLogService auditLogService,
        IClock clock,
        IHostEnvironment env,
        ILogger<FeedbackService> logger)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _auditLogService = auditLogService;
        _clock = clock;
        _env = env;
        _logger = logger;
    }

    public async Task<FeedbackReport> SubmitFeedbackAsync(
        Guid userId, FeedbackCategory category, string description,
        string pageUrl, string? userAgent, IFormFile? screenshot,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var reportId = Guid.NewGuid();

        var report = new FeedbackReport
        {
            Id = reportId,
            UserId = userId,
            Category = category,
            Description = description,
            PageUrl = pageUrl,
            UserAgent = userAgent,
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Handle screenshot upload
        if (screenshot is { Length: > 0 })
        {
            if (screenshot.Length > MaxScreenshotBytes)
                throw new InvalidOperationException("Screenshot must be under 10MB.");

            if (!AllowedContentTypes.Contains(screenshot.ContentType))
                throw new InvalidOperationException("Screenshot must be JPEG, PNG, or WebP.");

            var ext = screenshot.ContentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => throw new InvalidOperationException($"Unexpected content type: {screenshot.ContentType}")
            };

            var fileName = $"{Guid.NewGuid()}{ext}";
            var relativePath = Path.Combine("uploads", "feedback", reportId.ToString(), fileName);
            var absolutePath = Path.Combine(_env.ContentRootPath, "wwwroot", relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            await using var stream = new FileStream(absolutePath, FileMode.Create);
            await screenshot.CopyToAsync(stream, cancellationToken);

            report.ScreenshotFileName = screenshot.FileName;
            report.ScreenshotStoragePath = relativePath.Replace('\\', '/');
            report.ScreenshotContentType = screenshot.ContentType;
        }

        _dbContext.FeedbackReports.Add(report);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Feedback {ReportId} submitted by {UserId}: {Category}", reportId, userId, category);

        return report;
    }

    public async Task<FeedbackReport?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.FeedbackReports
            .Include(f => f.User)
            .Include(f => f.ResolvedByUser)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.FeedbackReports
            .Include(f => f.User)
            .Include(f => f.ResolvedByUser)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(f => f.Status == status.Value);

        if (category.HasValue)
            query = query.Where(f => f.Category == category.Value);

        return await query
            .OrderByDescending(f => f.CreatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var now = _clock.GetCurrentInstant();
        report.Status = status;
        report.UpdatedAt = now;

        if (status is FeedbackStatus.Resolved or FeedbackStatus.WontFix)
        {
            report.ResolvedAt = now;
            report.ResolvedByUserId = actorUserId;
        }
        else
        {
            // Reopening — clear resolved fields
            report.ResolvedAt = null;
            report.ResolvedByUserId = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        string actorName = "API";
        if (actorUserId.HasValue)
        {
            var actor = await _dbContext.Users.FindAsync(new object[] { actorUserId.Value }, cancellationToken);
            actorName = actor?.DisplayName ?? actorUserId.Value.ToString();
        }

        await _auditLogService.LogAsync(
            AuditAction.FeedbackStatusChanged, nameof(FeedbackReport), id,
            $"Feedback {id} status changed to {status}",
            actorUserId ?? Guid.Empty, actorName);

        _logger.LogInformation("Feedback {ReportId} status changed to {Status} by {ActorId}", id, status, actorUserId);
    }

    public async Task UpdateAdminNotesAsync(
        Guid id, string? notes, CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        report.AdminNotes = notes;
        report.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        report.GitHubIssueNumber = issueNumber;
        report.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetResponseCountsAsync(
        IEnumerable<Guid> reportIds, CancellationToken cancellationToken = default)
    {
        var idList = reportIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<Guid, int>();

        return await _dbContext.AuditLogEntries
            .Where(a => a.Action == AuditAction.FeedbackResponseSent
                && a.EntityType == nameof(FeedbackReport)
                && idList.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken);
    }

    public async Task SendResponseAsync(
        Guid id, string message, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports
            .Include(f => f.User)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        var user = report.User;

        await _emailService.SendFeedbackResponseAsync(
            user.Email!, user.DisplayName,
            report.Description, message,
            user.PreferredLanguage, cancellationToken);

        report.AdminResponseSentAt = _clock.GetCurrentInstant();
        report.UpdatedAt = report.AdminResponseSentAt.Value;
        await _dbContext.SaveChangesAsync(cancellationToken);

        string actorName = "API";
        if (actorUserId.HasValue)
        {
            var actor = await _dbContext.Users.FindAsync(new object[] { actorUserId.Value }, cancellationToken);
            actorName = actor?.DisplayName ?? actorUserId.Value.ToString();
        }

        if (actorUserId.HasValue)
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackResponseSent, nameof(FeedbackReport), id,
                $"Response sent to {user.DisplayName} for feedback {id}",
                actorUserId.Value, actorName);
        }
        else
        {
            await _auditLogService.LogAsync(
                AuditAction.FeedbackResponseSent, nameof(FeedbackReport), id,
                $"Response sent to {user.DisplayName} for feedback {id}",
                actorName);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Feedback response sent for {ReportId} by {ActorId}", id, actorUserId);
    }

    public async Task<IReadOnlyList<FeedbackResponseDetail>> GetResponseDetailsAsync(
        Guid reportId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AuditLogEntries
            .Where(a => a.Action == AuditAction.FeedbackResponseSent
                && a.EntityType == nameof(FeedbackReport)
                && a.EntityId == reportId)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => new FeedbackResponseDetail(
                a.OccurredAt.ToDateTimeUtc(),
                a.ActorName))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
}

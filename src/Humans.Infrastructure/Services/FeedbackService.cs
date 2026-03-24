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
        string pageUrl, string? userAgent, string? additionalContext,
        IFormFile? screenshot, CancellationToken cancellationToken = default)
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
            AdditionalContext = additionalContext,
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
            .Include(f => f.Messages.OrderBy(m => m.CreatedAt))
                .ThenInclude(m => m.SenderUser)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.FeedbackReports
            .Include(f => f.User)
            .Include(f => f.ResolvedByUser)
            .Include(f => f.Messages)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(f => f.Status == status.Value);

        if (category.HasValue)
            query = query.Where(f => f.Category == category.Value);

        if (reporterUserId.HasValue)
            query = query.Where(f => f.UserId == reporterUserId.Value);

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

    public async Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {id} not found");

        report.GitHubIssueNumber = issueNumber;
        report.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content, bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.FeedbackReports
            .Include(f => f.User)
            .FirstOrDefaultAsync(f => f.Id == reportId, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback report {reportId} not found");

        var now = _clock.GetCurrentInstant();
        var message = new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            SenderUserId = senderUserId,
            Content = content,
            CreatedAt = now
        };
        _dbContext.FeedbackMessages.Add(message);

        if (isAdmin)
        {
            report.LastAdminMessageAt = now;
            var user = report.User;
            var reportLink = $"/Feedback/{reportId}";
            await _emailService.SendFeedbackResponseAsync(
                user.Email!, user.DisplayName,
                report.Description, content, reportLink,
                user.PreferredLanguage, cancellationToken);
        }
        else
        {
            report.LastReporterMessageAt = now;
        }

        report.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Feedback message posted on {ReportId} by {UserId} (admin: {IsAdmin})", reportId, senderUserId, isAdmin);
        return message;
    }

    public async Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.FeedbackMessages
            .Include(m => m.SenderUser)
            .Where(m => m.FeedbackReportId == reportId)
            .OrderBy(m => m.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.FeedbackReports
            .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
            .CountAsync(f =>
                (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
                (f.LastReporterMessageAt != null && (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)),
                cancellationToken);
    }
}

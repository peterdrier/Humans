using System.Text.RegularExpressions;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Notifications;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Camps;

public class CampContactService : ICampContactService
{
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CampContactService> _logger;

    public CampContactService(
        IEmailService emailService,
        IAuditLogService auditLogService,
        INotificationEmitter notificationEmitter,
        IMemoryCache cache,
        ILogger<CampContactService> logger)
    {
        _emailService = emailService;
        _auditLogService = auditLogService;
        _notificationEmitter = notificationEmitter;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CampContactResult> SendFacilitatedMessageAsync(
        Guid campId,
        string campContactEmail,
        string campDisplayName,
        Guid senderUserId,
        string senderDisplayName,
        string senderEmail,
        string message,
        bool includeContactInfo,
        IReadOnlyList<Guid> leadUserIds,
        string campDetailsUrl)
    {
        // Rate limit: one message per camp per user per 10 minutes
        var rateLimitKey = CacheKeys.CampContactRateLimit(senderUserId, campId);
        if (!await _cache.TryReserveAsync(rateLimitKey, TimeSpan.FromMinutes(10)))
        {
            return new CampContactResult(Success: false, RateLimited: true);
        }

        try
        {
            var cleanMessage = Regex.Replace(
                message, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1));

            await _emailService.SendFacilitatedMessageAsync(
                campContactEmail,
                campDisplayName,
                senderDisplayName,
                cleanMessage,
                includeContactInfo,
                senderEmail);

            await _auditLogService.LogAsync(
                AuditAction.FacilitatedMessageSent,
                nameof(Camp), campId,
                $"Message sent to camp '{campDisplayName}' (contact info shared: {(includeContactInfo ? "yes" : "no")})",
                senderUserId);

            await SendLeadNotificationAsync(campId, campDisplayName, leadUserIds, campDetailsUrl);

            return new CampContactResult(Success: true, RateLimited: false);
        }
        catch (Exception ex)
        {
            _cache.InvalidateCampContactRateLimit(senderUserId, campId);
            _logger.LogError(ex, "Failed to send facilitated message to camp {CampId}", campId);
            throw;
        }
    }

    private async Task SendLeadNotificationAsync(
        Guid campId,
        string campDisplayName,
        IReadOnlyList<Guid> leadUserIds,
        string campDetailsUrl)
    {
        if (leadUserIds.Count == 0)
        {
            return;
        }

        try
        {
            await _notificationEmitter.SendAsync(
                NotificationSource.FacilitatedMessageReceived,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"New message for {campDisplayName} - check your email",
                leadUserIds,
                actionUrl: campDetailsUrl,
                actionLabel: "View camp");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch FacilitatedMessageReceived notification for camp {CampId}", campId);
        }
    }
}

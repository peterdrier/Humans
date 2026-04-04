using System.Text.RegularExpressions;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

public class CampContactService : ICampContactService
{
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CampContactService> _logger;

    public CampContactService(
        IEmailService emailService,
        IAuditLogService auditLogService,
        IMemoryCache cache,
        ILogger<CampContactService> logger)
    {
        _emailService = emailService;
        _auditLogService = auditLogService;
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
        bool includeContactInfo)
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

            return new CampContactResult(Success: true, RateLimited: false);
        }
        catch (Exception ex)
        {
            _cache.InvalidateCampContactRateLimit(senderUserId, campId);
            _logger.LogError(ex, "Failed to send facilitated message to camp {CampId}", campId);
            throw;
        }
    }
}

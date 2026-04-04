using System.Net;
using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CommunicationPreferenceService : ICommunicationPreferenceService
{
    private const string ProtectorPurpose = "CommunicationPreference";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(90);

    private static readonly Dictionary<MessageCategory, bool> DefaultOptedOut = new()
    {
        [MessageCategory.System] = false,
        [MessageCategory.CampaignCodes] = false,
        [MessageCategory.FacilitatedMessages] = false,
        [MessageCategory.Ticketing] = false,
        [MessageCategory.VolunteerUpdates] = false,
        [MessageCategory.TeamUpdates] = false,
        [MessageCategory.Governance] = false,
        [MessageCategory.Marketing] = true,
    };

    private readonly HumansDbContext _db;
    private readonly ITimeLimitedDataProtector _protector;
    private readonly IClock _clock;
    private readonly IAuditLogService _auditLog;
    private readonly string _baseUrl;
    private readonly ILogger<CommunicationPreferenceService> _logger;

    public CommunicationPreferenceService(
        HumansDbContext db,
        IDataProtectionProvider dataProtection,
        IClock clock,
        IAuditLogService auditLog,
        IOptions<EmailSettings> emailSettings,
        ILogger<CommunicationPreferenceService> logger)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _clock = clock;
        _auditLog = auditLog;
        _baseUrl = emailSettings.Value.BaseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommunicationPreference>> GetPreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.CommunicationPreferences
            .Where(cp => cp.UserId == userId)
            .ToListAsync(cancellationToken);

        var now = _clock.GetCurrentInstant();
        var created = false;

        foreach (var category in DefaultOptedOut.Keys)
        {
            if (existing.Any(cp => cp.Category == category))
                continue;

            var pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = DefaultOptedOut[category],
                UpdatedAt = now,
                UpdateSource = "Default",
            };
            _db.CommunicationPreferences.Add(pref);
            existing.Add(pref);
            created = true;
        }

        if (created)
            await _db.SaveChangesAsync(cancellationToken);

        return existing
            .OrderBy(cp => cp.Category)
            .ToList()
            .AsReadOnly();
    }

    public async Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default)
    {
        // Always-on categories can never be opted out of
        if (category.IsAlwaysOn())
            return false;

        var pref = await _db.CommunicationPreferences
            .FirstOrDefaultAsync(
                cp => cp.UserId == userId && cp.Category == category,
                cancellationToken);

        // If no preference row, use the default
        return pref?.OptedOut ?? DefaultOptedOut.GetValueOrDefault(category, false);
    }

    public async Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default)
    {
        // Always-on categories cannot be opted out of
        if (category.IsAlwaysOn())
        {
            _logger.LogWarning("Attempted to change always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        var now = _clock.GetCurrentInstant();

        var pref = await _db.CommunicationPreferences
            .FirstOrDefaultAsync(
                cp => cp.UserId == userId && cp.Category == category,
                cancellationToken);

        if (pref is null)
        {
            pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                UpdatedAt = now,
                UpdateSource = source,
            };
            _db.CommunicationPreferences.Add(pref);
        }
        else
        {
            if (pref.OptedOut == optedOut)
                return; // idempotent — no change needed

            pref.OptedOut = optedOut;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
        }

        var description = optedOut
            ? $"{category} opted out via {source}"
            : $"{category} opted in via {source}";

        await _auditLog.LogAsync(
            AuditAction.CommunicationPreferenceChanged,
            "User", userId, description,
            "CommunicationPreferenceService");

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} communication preference {Category} set to OptedOut={OptedOut} via {Source}",
            userId, category, optedOut, source);
    }

    public async Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        CancellationToken cancellationToken = default)
    {
        // Always-on categories cannot be opted out of
        if (category.IsAlwaysOn())
        {
            _logger.LogWarning("Attempted to change always-on preference {Category} for user {UserId} — ignored", category, userId);
            return;
        }

        var now = _clock.GetCurrentInstant();

        var pref = await _db.CommunicationPreferences
            .FirstOrDefaultAsync(
                cp => cp.UserId == userId && cp.Category == category,
                cancellationToken);

        if (pref is null)
        {
            pref = new CommunicationPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Category = category,
                OptedOut = optedOut,
                InboxEnabled = inboxEnabled,
                UpdatedAt = now,
                UpdateSource = source,
            };
            _db.CommunicationPreferences.Add(pref);
        }
        else
        {
            if (pref.OptedOut == optedOut && pref.InboxEnabled == inboxEnabled)
                return; // idempotent — no change needed

            pref.OptedOut = optedOut;
            pref.InboxEnabled = inboxEnabled;
            pref.UpdatedAt = now;
            pref.UpdateSource = source;
        }

        var description = $"{category} set to OptedOut={optedOut}, InboxEnabled={inboxEnabled} via {source}";

        await _auditLog.LogAsync(
            AuditAction.CommunicationPreferenceChanged,
            "User", userId, description,
            "CommunicationPreferenceService");

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "User {UserId} communication preference {Category} set to OptedOut={OptedOut}, InboxEnabled={InboxEnabled} via {Source}",
            userId, category, optedOut, inboxEnabled, source);
    }

    public string GenerateUnsubscribeToken(Guid userId, MessageCategory category)
    {
        var payload = $"{userId}|{category}";
        return _protector.Protect(payload, TokenLifetime);
    }

    public (Guid UserId, MessageCategory Category)? ValidateUnsubscribeToken(string token)
    {
        try
        {
            var payload = _protector.Unprotect(token);
            var parts = payload.Split('|');
            if (parts.Length != 2)
                return null;

            if (!Guid.TryParse(parts[0], out var userId))
                return null;

            if (!Enum.TryParse<MessageCategory>(parts[1], out var category))
                return null;

            return (userId, category);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to validate unsubscribe token");
            return null;
        }
    }

    public async Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        return !await IsOptedOutAsync(userId, MessageCategory.FacilitatedMessages, cancellationToken);
    }

    public Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category)
    {
        var token = GenerateUnsubscribeToken(userId, category);
        var oneClickUrl = $"{_baseUrl}/Unsubscribe/OneClick?token={WebUtility.UrlEncode(token)}";
        var browserUrl = $"{_baseUrl}/Unsubscribe/{Uri.EscapeDataString(token)}";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["List-Unsubscribe"] = $"<{oneClickUrl}>, <{browserUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };
    }
}

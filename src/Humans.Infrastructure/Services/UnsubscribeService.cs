using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

public class UnsubscribeService : IUnsubscribeService
{
    private readonly HumansDbContext _db;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<UnsubscribeService> _logger;

    public UnsubscribeService(
        HumansDbContext db,
        ICommunicationPreferenceService preferenceService,
        IDataProtectionProvider dataProtection,
        ILogger<UnsubscribeService> logger)
    {
        _db = db;
        _preferenceService = preferenceService;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    public async Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        // Try new category-aware token first
        var result = _preferenceService.ValidateUnsubscribeToken(token);
        if (result is not null)
        {
            var (userId, category) = result.Value;
            var user = await _db.Users.FindAsync(new object[] { userId }, ct);
            if (user is null)
                return UnsubscribeTokenResult.Invalid();

            return UnsubscribeTokenResult.Valid(userId, user.DisplayName, category);
        }

        // Fall back to legacy campaign-only token
        return await ValidateLegacyTokenAsync(token, ct);
    }

    public async Task<UnsubscribeTokenResult> ConfirmUnsubscribeAsync(string token, string source, CancellationToken ct = default)
    {
        // Try new category-aware token first
        var result = _preferenceService.ValidateUnsubscribeToken(token);
        if (result is not null)
        {
            var (userId, category) = result.Value;
            var user = await _db.Users.FindAsync(new object[] { userId }, ct);
            if (user is null)
                return UnsubscribeTokenResult.Invalid();

            await _preferenceService.UpdatePreferenceAsync(userId, category, optedOut: true, source: source);
            return UnsubscribeTokenResult.Valid(userId, user.DisplayName, category);
        }

        // Fall back to legacy campaign-only token
        var legacyResult = await ValidateLegacyTokenAsync(token, ct);
        if (legacyResult.IsValid && legacyResult.UserId.HasValue)
        {
            await _preferenceService.UpdatePreferenceAsync(
                legacyResult.UserId.Value, MessageCategory.Marketing, optedOut: true, source: source);
        }

        return legacyResult;
    }

    private async Task<UnsubscribeTokenResult> ValidateLegacyTokenAsync(string token, CancellationToken ct)
    {
        var protector = _dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();

        Guid userId;
        try
        {
            var userIdString = protector.Unprotect(token);
            userId = Guid.Parse(userIdString);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Unsubscribe token was expired or invalid");
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return UnsubscribeTokenResult.Expired();
            return UnsubscribeTokenResult.Invalid();
        }

        var user = await _db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null)
            return UnsubscribeTokenResult.Invalid();

        return UnsubscribeTokenResult.Valid(userId, user.DisplayName, MessageCategory.Marketing, isLegacy: true);
    }
}

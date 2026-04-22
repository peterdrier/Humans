using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Users;

/// <summary>
/// Validates unsubscribe tokens (new category-aware and legacy campaign-only)
/// and applies the unsubscribe action.
/// </summary>
/// <remarks>
/// Application-layer implementation — goes through <see cref="IUserRepository"/>,
/// never injects <c>DbContext</c>.
/// </remarks>
public sealed class UnsubscribeService : IUnsubscribeService
{
    private readonly IUserRepository _userRepository;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<UnsubscribeService> _logger;

    public UnsubscribeService(
        IUserRepository userRepository,
        ICommunicationPreferenceService preferenceService,
        IDataProtectionProvider dataProtection,
        ILogger<UnsubscribeService> logger)
    {
        _userRepository = userRepository;
        _preferenceService = preferenceService;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    public async Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        // Try new category-aware token first
        var result = _preferenceService.ValidateUnsubscribeToken(token);
        if (result.Status == TokenValidationStatus.Valid)
        {
            var user = await _userRepository.GetByIdAsync(result.UserId, ct);
            if (user is null)
                return UnsubscribeTokenResult.Invalid();

            return UnsubscribeTokenResult.Valid(result.UserId, user.DisplayName, result.Category);
        }

        // Expired new-format token — don't fall through to legacy
        if (result.Status == TokenValidationStatus.Expired)
            return UnsubscribeTokenResult.Expired();

        // Not a new-format token at all — fall back to legacy campaign-only token
        return await ValidateLegacyTokenAsync(token, ct);
    }

    public async Task<UnsubscribeTokenResult> ConfirmUnsubscribeAsync(string token, string source, CancellationToken ct = default)
    {
        var result = await ValidateTokenAsync(token, ct);
        if (!result.IsValid || !result.UserId.HasValue || !result.Category.HasValue)
            return result;

        await _preferenceService.UpdatePreferenceAsync(
            result.UserId.Value, result.Category.Value, optedOut: true, source: source);

        return result;
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
            // CommunicationPreferenceService already logged the first attempt at Information.
            // Don't double-log here — this is the legacy-protector fallback for the same event. See #483.
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return UnsubscribeTokenResult.Expired();

            _logger.LogDebug(ex, "Legacy unsubscribe token failed to unprotect");
            return UnsubscribeTokenResult.Invalid();
        }

        var user = await _userRepository.GetByIdAsync(userId, ct);
        if (user is null)
            return UnsubscribeTokenResult.Invalid();

        return UnsubscribeTokenResult.Valid(userId, user.DisplayName, MessageCategory.Marketing, isLegacy: true);
    }
}

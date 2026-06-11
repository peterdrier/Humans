using System.Security.Cryptography;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.Users;

public sealed class UnsubscribeService(
    IUserRepository userRepository,
    IUserServiceRead userService,
    ICommunicationPreferenceService preferenceService,
    IDataProtectionProvider dataProtection,
    ILogger<UnsubscribeService> logger) : IUnsubscribeService
{
    public async Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var result = preferenceService.ValidateUnsubscribeToken(token);
        if (result.Status == TokenValidationStatus.Valid)
        {
            var user = await userRepository.GetByIdAsync(result.UserId, ct);
            if (user is null)
                return UnsubscribeTokenResult.Invalid();

            var info = await userService.GetUserInfoAsync(result.UserId, ct);
            return UnsubscribeTokenResult.Valid(result.UserId, info?.BurnerName ?? string.Empty, result.Category);
        }

        if (result.Status == TokenValidationStatus.Expired)
            return UnsubscribeTokenResult.Expired();

        var protector = dataProtection
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
            // No double-log — already logged in CommunicationPreferenceService (see #483).
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return UnsubscribeTokenResult.Expired();

            logger.LogDebug(ex, "Legacy unsubscribe token failed to unprotect");
            return UnsubscribeTokenResult.Invalid();
        }

        var legacyUser = await userRepository.GetByIdAsync(userId, ct);
        if (legacyUser is null)
            return UnsubscribeTokenResult.Invalid();

        var legacyInfo = await userService.GetUserInfoAsync(userId, ct);
        return UnsubscribeTokenResult.Valid(userId, legacyInfo?.BurnerName ?? string.Empty, MessageCategory.Marketing, isLegacy: true);
    }

    public async Task<UnsubscribeTokenResult> ConfirmUnsubscribeAsync(string token, string source, CancellationToken ct = default)
    {
        var result = await ValidateTokenAsync(token, ct);
        if (!result.IsValid || !result.UserId.HasValue || !result.Category.HasValue)
            return result;

        await preferenceService.UpdatePreferenceAsync(
            result.UserId.Value, result.Category.Value, optedOut: true, source: source);

        return result;
    }
}

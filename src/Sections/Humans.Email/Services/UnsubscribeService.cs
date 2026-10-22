using System.Security.Cryptography;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Email.Services;

internal sealed class UnsubscribeService(
    IUserService userService,
    ICommunicationPreferenceManagement preferenceService,
    IDataProtectionProvider dataProtection,
    ILogger<UnsubscribeService> logger) : IUnsubscribeService
{
    public async Task<UnsubscribeTokenResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return UnsubscribeTokenResult.Invalid();

        var result = preferenceService.ValidateUnsubscribeToken(token);
        if (result.Status == TokenValidationStatus.Valid)
        {
            // A token minted for a since-merged id acts on the survivor, never on the token's id.
            var info = await userService.GetUserInfoAsync(result.UserId, ct);
            if (info is null || info.IsGdprAnonymized)
                return UnsubscribeTokenResult.Invalid();

            return UnsubscribeTokenResult.Valid(info.Id, info.BurnerName, result.Category);
        }

        if (result.Status == TokenValidationStatus.Expired)
            return UnsubscribeTokenResult.Expired();

        var protector = dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();

        string userIdString;
        try
        {
            userIdString = protector.Unprotect(token);
        }
        catch (CryptographicException ex)
        {
            // UnsubscribeTokenProvider has already warned about this token.
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return UnsubscribeTokenResult.Expired();

            logger.LogDebug(ex, "Legacy unsubscribe token failed to unprotect");
            return UnsubscribeTokenResult.Invalid();
        }

        if (!Guid.TryParse(userIdString, out var userId))
            return UnsubscribeTokenResult.Invalid();

        var legacyInfo = await userService.GetUserInfoAsync(userId, ct);
        if (legacyInfo is null || legacyInfo.IsGdprAnonymized)
            return UnsubscribeTokenResult.Invalid();

        return UnsubscribeTokenResult.Valid(legacyInfo.Id, legacyInfo.BurnerName, MessageCategory.Marketing, isLegacy: true);
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

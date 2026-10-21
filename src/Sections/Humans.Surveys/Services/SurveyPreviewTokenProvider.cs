using System.Security.Cryptography;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Surveys.Services;

/// <summary>
/// Mints short-lived tokens for survey-preview email links. Preview tokens carry the survey id and
/// recipient culture, use a distinct Data Protection purpose from invitation tokens, and are accepted
/// only by the Board/Admin preview route.
/// </summary>
internal sealed class SurveyPreviewTokenProvider(IDataProtectionProvider dataProtection)
{
    private const string TokenPrefix = "preview.";
    private const string ProtectorPurpose = "SurveyPreview";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    public string Create(Guid surveyId, string culture)
    {
        if (!culture.IsSupportedCultureCode())
            throw new ArgumentException("A supported culture is required.", nameof(culture));

        return TokenPrefix + _protector.Protect($"{surveyId:D}\n{culture}", TokenLifetime);
    }

    public SurveyPreviewLink? Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;

        try
        {
            var payload = _protector.Unprotect(token[TokenPrefix.Length..]);
            var separator = payload.IndexOf('\n', StringComparison.Ordinal);
            var idText = separator < 0 ? payload : payload[..separator];
            if (!Guid.TryParse(idText, out var id))
                return null;

            var culture = separator < 0 ? null : payload[(separator + 1)..];
            return new SurveyPreviewLink(id, culture.IsSupportedCultureCode() ? culture : null);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Expired, tampered, and non-preview tokens are expected input on the public answer route.
            return null;
        }
    }
}

internal sealed record SurveyPreviewLink(Guid SurveyId, string? Culture);

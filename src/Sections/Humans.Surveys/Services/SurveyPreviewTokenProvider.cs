using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Surveys.Services;

/// <summary>
/// Mints short-lived tokens for survey-preview email links. Preview tokens carry only the survey id,
/// use a distinct Data Protection purpose from invitation tokens, and are accepted only by the
/// Board/Admin preview route.
/// </summary>
internal sealed class SurveyPreviewTokenProvider(IDataProtectionProvider dataProtection)
{
    private const string TokenPrefix = "preview.";
    private const string ProtectorPurpose = "SurveyPreview";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    public string Create(Guid surveyId)
        => TokenPrefix + _protector.Protect(surveyId.ToString(), TokenLifetime);

    public Guid? Resolve(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;

        try
        {
            var payload = _protector.Unprotect(token[TokenPrefix.Length..]);
            return Guid.TryParse(payload, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Expired, tampered, and non-preview tokens are expected input on the public answer route.
            return null;
        }
    }
}

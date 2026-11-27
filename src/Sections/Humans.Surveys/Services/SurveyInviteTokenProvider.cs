using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Humans.Surveys.Services;

internal sealed class SurveyInviteTokenProvider(IDataProtectionProvider dataProtection) : ISurveyInviteTokenProvider
{
    private const string ProtectorPurpose = "SurveyInvite";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(60);

    // The purpose ASP.NET's time-limited protector adds under ours. Unprotecting with it skips only the expiry
    // check, which is how an expired token is told apart from one that was never ours: the framework throws the
    // same exception type for both.
    private const string TimeLimitedPurpose = "Microsoft.AspNetCore.DataProtection.TimeLimitedDataProtector.v1";

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    private readonly IDataProtector _expiryBlindProtector =
        dataProtection.CreateProtector(ProtectorPurpose, TimeLimitedPurpose);

    public string Create(Guid invitationId) => _protector.Protect(invitationId.ToString(), TokenLifetime);

    public (Guid? InvitationId, SurveyLinkFailure? Failure) Resolve(string token)
    {
        // A missing ?t= reaches here as null/empty, not a 500 on an anonymous route.
        if (string.IsNullOrWhiteSpace(token)) return (null, SurveyLinkFailure.NoToken);

        string payload;
        try
        {
            payload = _protector.Unprotect(token);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return (null, IsAuthentic(token) ? SurveyLinkFailure.Expired : SurveyLinkFailure.Unreadable);
        }

        return Guid.TryParse(payload, out var id) ? (id, null) : (null, SurveyLinkFailure.Unreadable);
    }

    private bool IsAuthentic(string token)
    {
        try
        {
            // Bytes, not the string overload: the payload starts with the expiry ticks, which are not UTF-8.
            _expiryBlindProtector.Unprotect(WebEncoders.Base64UrlDecode(token));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}

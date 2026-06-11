using System.Security.Cryptography;
using Humans.Application.Interfaces.Surveys;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Infrastructure.Services.Surveys;

/// <summary>
/// ASP.NET Data Protection implementation of <see cref="ISurveyInviteTokenProvider"/>. The token
/// payload is just the invitation id; a per-survey lifetime tied to <c>ClosesAt</c> is a later
/// refinement (fixed 60-day lifetime is fine for v1).
/// </summary>
public sealed class SurveyInviteTokenProvider(IDataProtectionProvider dataProtection) : ISurveyInviteTokenProvider
{
    private const string ProtectorPurpose = "SurveyInvite";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(60);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    public string Create(Guid invitationId) => _protector.Protect(invitationId.ToString(), TokenLifetime);

    public Guid? Resolve(string token)
    {
        try
        {
            var payload = _protector.Unprotect(token);
            return Guid.TryParse(payload, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Expected: expired or tampered token. Signal "invalid" to the caller via null.
            return null;
        }
    }
}

using System.Net;
using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.Profiles;

/// <summary>
/// Infrastructure implementation of <see cref="IUnsubscribeTokenProvider"/>
/// using ASP.NET Core Data Protection for token generation/validation and
/// <see cref="EmailSettings"/> for URL building.
/// </summary>
public sealed class UnsubscribeTokenProvider : IUnsubscribeTokenProvider
{
    private const string ProtectorPurpose = "CommunicationPreference";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(90);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly string _baseUrl;
    private readonly ILogger<UnsubscribeTokenProvider> _logger;

    public UnsubscribeTokenProvider(
        IDataProtectionProvider dataProtection,
        IOptions<EmailSettings> emailSettings,
        ILogger<UnsubscribeTokenProvider> logger)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
        _baseUrl = emailSettings.Value.BaseUrl.TrimEnd('/');
        _logger = logger;
    }

    public string GenerateToken(Guid userId, MessageCategory category)
    {
        var payload = $"{userId}|{category}";
        return _protector.Protect(payload, TokenLifetime);
    }

    public (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateToken(string token)
    {
        try
        {
            var payload = _protector.Unprotect(token);
            var parts = payload.Split('|');
            if (parts.Length != 2)
                return (TokenValidationStatus.Invalid, default, default);

            if (!Guid.TryParse(parts[0], out var userId))
                return (TokenValidationStatus.Invalid, default, default);

            if (!Enum.TryParse<MessageCategory>(parts[1], out var category))
                return (TokenValidationStatus.Invalid, default, default);

            return (TokenValidationStatus.Valid, userId, category);
        }
        catch (CryptographicException ex)
        {
            var tokenPrefix = token.Length > 12 ? token[..12] : token;
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Expired unsubscribe token {TokenPrefix}", tokenPrefix);
                return (TokenValidationStatus.Expired, default, default);
            }

            _logger.LogWarning("Failed to validate unsubscribe token {TokenPrefix}", tokenPrefix);
            return (TokenValidationStatus.Invalid, default, default);
        }
    }

    public Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category)
    {
        var token = GenerateToken(userId, category);
        var oneClickUrl = $"{_baseUrl}/Unsubscribe/OneClick?token={WebUtility.UrlEncode(token)}";
        var browserUrl = $"{_baseUrl}/Unsubscribe/{Uri.EscapeDataString(token)}";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["List-Unsubscribe"] = $"<{oneClickUrl}>, <{browserUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };
    }

    public string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category)
    {
        var token = GenerateToken(userId, category);
        return $"{_baseUrl}/Unsubscribe/{token}";
    }
}

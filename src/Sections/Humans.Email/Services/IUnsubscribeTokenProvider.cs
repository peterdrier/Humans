using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>Status of an unsubscribe token validation attempt.</summary>
internal enum TokenValidationStatus
{
    /// <summary>Token is valid and decoded successfully.</summary>
    Valid,

    /// <summary>Token was a valid new-format token but has expired.</summary>
    Expired,

    /// <summary>Token is not a valid new-format token (tampered, corrupted, or different format).</summary>
    Invalid,
}

/// <summary>
/// Unsubscribe token generation, validation and URL building, over ASP.NET Core Data Protection
/// and <c>EmailSettings</c>. The protector purpose and payload are unchanged from when this lived in
/// Users, so tokens already sitting in members' inboxes keep validating.
/// </summary>
internal interface IUnsubscribeTokenProvider
{
    string GenerateToken(Guid userId, MessageCategory category);
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateToken(string token);
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);
}

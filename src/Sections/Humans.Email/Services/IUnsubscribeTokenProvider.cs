using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal enum TokenValidationStatus
{
    Valid,

    Expired,

    /// <summary>Tampered, corrupted, or not a category-aware token.</summary>
    Invalid,
}

/// <summary>
/// Never change the Data Protection purpose or payload: tokens already in members' inboxes must keep validating.
/// </summary>
internal interface IUnsubscribeTokenProvider
{
    string GenerateToken(Guid userId, MessageCategory category);
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateToken(string token);
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);
}

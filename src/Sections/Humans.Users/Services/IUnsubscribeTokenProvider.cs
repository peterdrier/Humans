using Humans.Users.Services;
using Humans.Domain.Enums;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Abstracts unsubscribe token generation, validation, and URL building.
/// Implemented in Infrastructure using <c>IDataProtectionProvider</c> and
/// <c>EmailSettings</c>. Allows <c>CommunicationPreferenceService</c> to
/// live in the Application layer without Infrastructure dependencies.
/// </summary>
internal interface IUnsubscribeTokenProvider
{
    string GenerateToken(Guid userId, MessageCategory category);
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateToken(string token);
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);
}

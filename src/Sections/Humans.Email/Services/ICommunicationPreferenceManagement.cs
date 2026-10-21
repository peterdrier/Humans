using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Email's own communication-preference surface: the public reads and writes plus what only Email
/// calls — the member's channel toggles, and the unsubscribe tokens and headers the send path stamps.
/// </summary>
internal interface ICommunicationPreferenceManagement
    : ICommunicationPreferenceService, ICommunicationPreferenceWrite, IApplicationService
{
    /// <summary>
    /// Updates opt-out and inbox-enabled status for a specific category. Idempotent.
    /// Logs the authenticated actor when supplied; anonymous updates retain service attribution.
    /// </summary>
    Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Generates a time-limited (~90 days) unsubscribe token encoding userId + category.</summary>
    string GenerateUnsubscribeToken(Guid userId, MessageCategory category);

    /// <summary>Validates and decodes an unsubscribe token.</summary>
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token);

    /// <summary>RFC 8058 List-Unsubscribe headers for a given user and category.</summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);

    /// <summary>A browser-friendly unsubscribe URL for email footers.</summary>
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);
}


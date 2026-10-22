using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

/// <summary>
/// Email's own communication-preference surface: the public reads and writes plus what only Email
/// calls — the member's channel toggles, the unsubscribe tokens and headers the send path stamps,
/// and the <c>email.communication-preferences</c> copy.
/// </summary>
internal interface ICommunicationPreferenceManagement
    : ICommunicationPreferenceService, ICommunicationPreferenceWrite, IApplicationService
{
    /// <summary>
    /// Updates opt-out and inbox-enabled status for a specific category. Idempotent.
    /// Logs the authenticated actor when supplied; anonymous updates retain service attribution.
    /// Returns <c>false</c>, writing nothing, when the category is Ticketing and locked because the
    /// member holds a matched ticket order.
    /// </summary>
    Task<bool> UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);

    /// <summary>Validates and decodes an unsubscribe token.</summary>
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token);

    /// <summary>RFC 8058 List-Unsubscribe headers for a given user and category.</summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);

    /// <summary>A browser-friendly unsubscribe URL for email footers.</summary>
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);

    /// <summary>Every legacy Users row with no row here yet for its user and category.</summary>
    Task<IReadOnlyList<UncopiedPreference>> GetUncopiedLegacyPreferencesAsync(CancellationToken ct = default);

    /// <summary>Copies <see cref="GetUncopiedLegacyPreferencesAsync"/>; idempotent. Returns rows written.</summary>
    Task<int> CopyLegacyPreferencesAsync(CancellationToken ct = default);
}

/// <summary>A legacy row the <c>email.communication-preferences</c> copy has still to write.</summary>
internal sealed record UncopiedPreference(Guid UserId, CommunicationPreferenceSnapshot Preference);

using Humans.Base.Interfaces;
using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal interface ICommunicationPreferenceManagement
    : ICommunicationPreferenceService, ICommunicationPreferenceWrite, IApplicationService
{
    /// <summary>
    /// Idempotent. Audits the actor when given, else the service. Returns false and writes nothing when an
    /// <see cref="ICommunicationPreferenceLock"/> locks the category for the human.
    /// </summary>
    Task<bool> UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);

    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token);

    /// <summary>RFC 8058 List-Unsubscribe headers.</summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);

    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);

    /// <summary>Every legacy Users row with no row here yet for its user and category.</summary>
    Task<IReadOnlyList<UncopiedPreference>> GetUncopiedLegacyPreferencesAsync(CancellationToken ct = default);

    /// <summary>Idempotent. Returns rows written.</summary>
    Task<int> CopyLegacyPreferencesAsync(CancellationToken ct = default);
}

internal sealed record UncopiedPreference(Guid UserId, CommunicationPreferenceSnapshot Preference);

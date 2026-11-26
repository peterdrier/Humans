using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal interface ICommunicationPreferenceManagement
    : ICommunicationPreferenceService, ICommunicationPreferenceWrite
{
    /// <summary>Recorded rows only; missing categories are left out.</summary>
    Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesReadOnlyAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent. Audits the actor when given, else the service. Refuses unavailable accounts and categories
    /// locked by <see cref="ICommunicationPreferenceLock"/> without writing.
    /// </summary>
    Task<PreferenceUpdateOutcome> UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        Guid? actorUserId = null, CancellationToken cancellationToken = default);

    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token);

    /// <summary>RFC 8058 List-Unsubscribe headers.</summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);

    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);
}

internal enum PreferenceUpdateOutcome
{
    UserUnavailable,
    Success,
    Locked
}

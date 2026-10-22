using Humans.Base.Interfaces;

namespace Humans.Cantina.Contracts;

/// <summary>Medical conditions are not here: they are GDPR Art. 9 data owned by Users.</summary>
public interface ICantinaService : IApplicationService
{
    /// <summary>Null when the member has never given one.</summary>
    Task<DietaryProfileInfo?> GetDietaryProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Members without one are absent.</summary>
    Task<IReadOnlyDictionary<Guid, DietaryProfileInfo>> GetDietaryProfilesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary>Option values are the canonical English chip values. Whether to prompt the member is each caller's call,
/// composed from these facts.</summary>
public sealed record DietaryProfileInfo(
    Guid UserId,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText,
    bool DeclinedToAnswer = false)
{
    public bool HasPreference => !string.IsNullOrEmpty(DietaryPreference);
}

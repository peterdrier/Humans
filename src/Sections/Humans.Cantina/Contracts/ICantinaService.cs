using Humans.Base.Interfaces;

namespace Humans.Cantina.Contracts;

/// <summary>
/// Cantina's member-keyed reads: what each member can and cannot eat
/// (<c>cantina_dietary_profiles</c>). Medical conditions are not here; they are GDPR Art. 9
/// data owned by Users and gated by <c>MedicalDataViewer</c>.
/// </summary>
public interface ICantinaService : IApplicationService
{
    /// <summary>The member's dietary profile, or null when they have never given one.</summary>
    Task<DietaryProfileInfo?> GetDietaryProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Dietary profiles for many members in one read; members without one are absent.</summary>
    Task<IReadOnlyDictionary<Guid, DietaryProfileInfo>> GetDietaryProfilesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <summary>A member's food answers. Option values are the canonical English chip values.
/// <c>DeclinedToAnswer</c> records "I don't want to answer" (every food field is then empty).
/// Whether to prompt the member is each caller's call, composed from these facts.</summary>
public sealed record DietaryProfileInfo(
    Guid UserId,
    string? DietaryPreference,
    IReadOnlyList<string> Allergies,
    string? AllergyOtherText,
    IReadOnlyList<string> Intolerances,
    string? IntoleranceOtherText,
    bool DeclinedToAnswer = false)
{
    /// <summary>True once the member has picked a preference.</summary>
    public bool HasPreference => !string.IsNullOrEmpty(DietaryPreference);
}

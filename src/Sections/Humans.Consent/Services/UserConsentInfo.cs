
namespace Humans.Consent.Services;

/// <summary>One user's cached consented version ids.</summary>
/// <param name="UserId">The cache key.</param>
/// <param name="ConsentedVersionIds">Unioned across the merge chain. Read-only; safe to share across requests.</param>
/// <param name="UserIds">The id list the set was built over, read before the load. A different current list means a merge since.</param>
internal sealed record UserConsentInfo(
    Guid UserId,
    IReadOnlySet<Guid> ConsentedVersionIds,
    IReadOnlyList<Guid> UserIds);

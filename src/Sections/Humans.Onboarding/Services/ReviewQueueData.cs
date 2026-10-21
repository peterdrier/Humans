using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Onboarding.Services;

internal sealed record ReviewQueueData(
    List<UserInfo> Pending,
    List<UserInfo> Flagged,
    HashSet<Guid> PendingAppUserIds,
    Dictionary<Guid, ConsentProgressInfo> ConsentProgress,
    IReadOnlyDictionary<Guid, MembershipTier> Tiers,
    IReadOnlyDictionary<Guid, ConsentReviewInfo> Reviews);

internal sealed record ConsentProgressInfo(int Signed, int Required);

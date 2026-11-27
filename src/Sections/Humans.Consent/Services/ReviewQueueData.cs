using Humans.Consent.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Consent.Services;

internal sealed record ReviewQueueData(
    List<UserInfo> Pending,
    List<UserInfo> Flagged,
    Dictionary<Guid, ConsentProgressInfo> ConsentProgress,
    IReadOnlyDictionary<Guid, MembershipTier> Tiers);

internal sealed record ConsentProgressInfo(int Signed, int Required);

using Humans.Application;
using Humans.Users.Contracts;

namespace Humans.Onboarding.Services;

internal sealed record ReviewQueueData(
    List<UserInfo> Pending,
    List<UserInfo> Flagged,
    HashSet<Guid> PendingAppUserIds,
    Dictionary<Guid, ConsentProgressInfo> ConsentProgress);

internal sealed record ConsentProgressInfo(int Signed, int Required);

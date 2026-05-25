namespace Humans.Application.DTOs;

public record ReviewQueueData(
    List<UserInfo> Pending,
    List<UserInfo> Flagged,
    HashSet<Guid> PendingAppUserIds,
    Dictionary<Guid, ConsentProgressInfo> ConsentProgress);

public record ConsentProgressInfo(int Signed, int Required);

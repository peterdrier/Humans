using Humans.Domain.Entities;

namespace Humans.Application.DTOs;

public record ReviewQueueData(
    List<Profile> Pending,
    List<Profile> Flagged,
    HashSet<Guid> PendingAppUserIds,
    Dictionary<Guid, ConsentProgressInfo> ConsentProgress);

public record ConsentProgressInfo(int Signed, int Required);

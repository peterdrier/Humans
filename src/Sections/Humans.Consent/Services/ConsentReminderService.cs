using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// No <c>IUserMerge</c>: the job reads by the resolved survivor id.
/// </summary>
internal sealed class ConsentReminderService(
    IConsentReminderRepository repo,
    IUserService users) : IApplicationService, IUserDataContributor
{
    internal const string ConsentReminderSlice = "ConsentReminder";

    /// <summary>Members never reminded are absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, Instant>> GetLastSentAsync(
        IEnumerable<UserInfo> members, CancellationToken ct = default)
    {
        var ids = members.Select(m => m.Id).Distinct().ToList();
        return (await repo.GetByUserIdsAsync(ids, ct)).ToDictionary(r => r.UserId, r => r.LastSentAt);
    }

    public Task RecordSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.UpsertAsync(userId, sentAt, ct);

    /// <summary>Survivor first, then merged-away ids.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var survivorId = info?.Id ?? userId;
        var stamps = (await repo.GetByUserIdsAsync(info?.AllUserIds ?? [userId], ct))
            .Select(r => (r.UserId, SentAt: r.LastSentAt))
            .ToList();
        object? data = stamps.Count == 0
            ? null
            : stamps.OrderBy(s => s.UserId == survivorId ? 0 : 1)
                .Select(s => new { s.UserId, LastSentAt = s.SentAt.ToIso8601() })
                .ToList();
        return [new UserDataSlice(ConsentReminderSlice, data)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [ConsentReminderSlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.DeleteAsync(userId, ct);
}

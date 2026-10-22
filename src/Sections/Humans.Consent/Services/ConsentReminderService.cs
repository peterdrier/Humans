using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Until the <c>consent.reminder-stamps</c> copy has run, a member without a row reads their legacy
/// <c>users.LastConsentReminderSentAt</c>. No <c>IUserMerge</c>: the job reads by the resolved survivor id.
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
        var byId = members.DistinctBy(m => m.Id).ToDictionary(m => m.Id);
        var result = (await repo.GetByUserIdsAsync(byId.Keys, ct)).ToDictionary(r => r.UserId, r => r.LastSentAt);
        foreach (var member in byId.Values)
        {
            if (!result.ContainsKey(member.Id) && LegacyLastSent(member) is { } legacy)
                result[member.Id] = legacy;
        }
        return result;
    }

    public Task RecordSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.UpsertAsync(userId, sentAt, ct);

    internal sealed record LegacyStamp(Guid UserId, Instant SentAt);

    /// <summary>Tombstones are skipped: they are never reminded, and copying an erased member's stamp would
    /// resurrect it.</summary>
    public async Task<IReadOnlyList<LegacyStamp>> GetUncopiedLegacyStampsAsync(CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        return [.. (await users.GetAllUserInfosAsync(ct))
            .Where(u => !u.IsTombstone && !copied.Contains(u.Id))
            .OrderBy(u => u.CreatedAt)
            .Select(u => LegacyLastSent(u) is { } sentAt ? new LegacyStamp(u.Id, sentAt) : null)
            .OfType<LegacyStamp>()];
    }

    public async Task<int> CopyLegacyStampsAsync(CancellationToken ct = default)
    {
        var written = 0;
        foreach (var stamp in await GetUncopiedLegacyStampsAsync(ct))
        {
            if (await repo.TryInsertAsync(stamp.UserId, stamp.SentAt, ct))
                written++;
        }
        return written;
    }

    /// <summary>The only reader of the obsolete legacy field; it goes when the data migration is retired. A tombstone
    /// reads null: members erased before Users' erasure cleared the column still hold a stamp there.</summary>
    private static Instant? LegacyLastSent(UserInfo? info)
    {
#pragma warning disable HUM_USER_CONSENT_REMINDER // read fallback and copy, until the data migration is retired
        return info is { IsTombstone: false } ? info.LastConsentReminderSentAt : null;
#pragma warning restore HUM_USER_CONSENT_REMINDER
    }

    /// <summary>Survivor first, then merged-away ids. The survivor's legacy stamp stands in for its row until the
    /// copy has run.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var survivorId = info?.Id ?? userId;
        var stamps = (await repo.GetByUserIdsAsync(info?.AllUserIds ?? [userId], ct))
            .Select(r => (r.UserId, SentAt: r.LastSentAt))
            .ToList();
        if (stamps.TrueForAll(s => s.UserId != survivorId) && LegacyLastSent(info) is { } legacy)
            stamps.Add((survivorId, legacy));

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

    /// <summary>Users' erasure clears the legacy column, and the fallback reads null for a tombstone.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.DeleteAsync(userId, ct);
}

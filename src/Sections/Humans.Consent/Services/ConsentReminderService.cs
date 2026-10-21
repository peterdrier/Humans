using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// The re-consent reminder cooldown stamps Consent owns (<c>consent_reminders</c>): the read and
/// write behind <c>SendReConsentReminderJob</c>, the <c>consent.reminder-stamps</c> copy and the
/// Article 15/17 slice. Until the copy has run, a member without a row reads their legacy
/// <c>users.LastConsentReminderSentAt</c> through <see cref="UserInfo"/> (Users decomposition
/// plan §3.1 step 4).
/// </summary>
/// <remarks>
/// No <c>IUserMerge</c>: rows stay keyed to the id they were written for. The job reads by the
/// resolved survivor id, so a merged-away id's stamp is never consulted, as when this was a
/// <c>users</c> column the merge never moved.
/// </remarks>
internal sealed class ConsentReminderService(
    IConsentReminderRepository repo,
    IUserService users) : IApplicationService, IUserDataContributor
{
    internal const string ConsentReminderSlice = "ConsentReminder";

    /// <summary>When each of <paramref name="members"/> was last reminded; members never reminded
    /// are absent.</summary>
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

    /// <summary>Stamps the member's cooldown anchor.</summary>
    public Task RecordSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.UpsertAsync(userId, sentAt, ct);

    // ─── consent.reminder-stamps ─────────────────────────────────────────────────

    /// <summary>A legacy stamp the copy would write.</summary>
    internal sealed record LegacyStamp(Guid UserId, Instant SentAt);

    /// <summary>Every living member whose legacy stamp has no row yet. Tombstones are skipped: they
    /// are never reminded, and copying an erased member's stamp would resurrect it.</summary>
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

    /// <summary>The one reader of the obsolete legacy field: the read fallback, the copy and the
    /// export. Deleted with the migration once production reports it done.</summary>
    private static Instant? LegacyLastSent(UserInfo? info)
    {
#pragma warning disable HUM_USER_CONSENT_REMINDER // the fallback and copy the field is kept for (plan §3.1)
        return info?.LastConsentReminderSentAt;
#pragma warning restore HUM_USER_CONSENT_REMINDER
    }

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    /// <summary>The stamps of this human's own id and every merged-away id, survivor first; the
    /// survivor's legacy stamp stands in for its row until the copy has run.</summary>
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

    /// <summary>Deletes the member's row. The legacy <c>users</c> column stays with Users, which
    /// still owns it until the drop.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.DeleteAsync(userId, ct);
}

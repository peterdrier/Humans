using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Consent.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Consent's re-consent reminder cooldown stamps (<c>consent_reminders</c>): the read and write
/// behind <c>SendReConsentReminderJob</c> and the Article 15/17 slice.
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
        var ids = members.Select(m => m.Id).Distinct().ToList();
        return (await repo.GetByUserIdsAsync(ids, ct)).ToDictionary(r => r.UserId, r => r.LastSentAt);
    }

    /// <summary>Stamps the member's cooldown anchor.</summary>
    public Task RecordSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
        repo.UpsertAsync(userId, sentAt, ct);

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    /// <summary>The stamps of this human's own id and every merged-away id, survivor first.</summary>
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

    /// <summary>Deletes the member's reminder row.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.DeleteAsync(userId, ct);
}

using Humans.Base.Extensions;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Gdpr.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Consent.Services;

/// <summary>
/// Until the <c>consent.profile-review</c> copy has run, a member without a row reads their legacy
/// <c>profiles</c> review columns. No <c>IUserMerge</c>: reads resolve a merged-away id forward, so the
/// survivor's own review stands.
/// </summary>
internal sealed class ConsentReviewService(
    IConsentReviewRepository repo,
    IUserService users,
    IUserWrite userWrite,
    IClock clock) : IConsentWrite, IUserDataContributor
{
    internal const string ConsentReviewSlice = "ConsentReview";

    public async Task<ConsentReviewInfo> GetReviewAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        var row = await repo.GetAsync(id, ct);
        return row is not null ? ToInfo(row) : LegacyReview(info) ?? ConsentReviewInfo.None(id);
    }

    public async Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetReviewsAsync(CancellationToken ct = default)
    {
        var result = (await repo.GetAllAsync(ct)).ToDictionary(r => r.UserId, ToInfo);
        foreach (var info in await users.GetAllRawUserInfosAsync(ct))
        {
            if (!result.ContainsKey(info.Id) && LegacyReview(info) is { } legacy)
                result[info.Id] = legacy;
        }
        return result;
    }

    public Task<OnboardingResult> RecordConsentCheckAsync(
        Guid userId, ConsentCheckStatus status, Guid? reviewerId, string? notes, CancellationToken ct = default)
    {
        if (status is not (ConsentCheckStatus.Cleared or ConsentCheckStatus.Flagged))
            throw new ArgumentException(
                "RecordConsentCheck only accepts Cleared or Flagged; use SetConsentCheckPending for the system-driven Pending transition.",
                nameof(status));

        return MutateAsync(userId, (row, now) =>
        {
            var cleared = status == ConsentCheckStatus.Cleared;
            if (cleared && row.RejectedAt is not null)
                return "AlreadyRejected";
            row.Status = status;
            row.CheckedAt = now;
            row.CheckedByUserId = reviewerId;
            row.Notes = notes;
            row.IsApproved = cleared;
            return null;
        }, ct);
    }

    public Task<OnboardingResult> SetConsentCheckPendingAsync(Guid userId, CancellationToken ct = default) =>
        MutateAsync(userId, (row, _) =>
        {
            row.Status = ConsentCheckStatus.Pending;
            return null;
        }, ct);

    public Task<OnboardingResult> RecordRejectionAsync(
        Guid userId, Guid reviewerId, string? reason, CancellationToken ct = default) =>
        MutateAsync(userId, (row, now) =>
        {
            if (row.RejectedAt is not null)
                return "AlreadyRejected";
            row.RejectionReason = reason;
            row.RejectedAt = now;
            row.RejectedByUserId = reviewerId;
            row.IsApproved = false;
            return null;
        }, ct);

    // A member with no row starts from their legacy review, so a write never drops what the profile columns
    // still hold. Keyed to the id asked with.
    private async Task<OnboardingResult> MutateAsync(
        Guid userId, Func<ConsentReview, Instant, string?> apply, CancellationToken ct)
    {
        var info = await users.GetRawUserInfoAsync(userId, ct);
        if (info?.Profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = clock.GetCurrentInstant();
        var row = await repo.GetAsync(userId, ct) ?? FromInfo(LegacyReview(info) ?? ConsentReviewInfo.None(userId), now);
        var error = apply(row, now);
        if (error is not null)
            return new OnboardingResult(false, error);

        row.UpdatedAt = now;
        await repo.UpsertAsync(row, ct);
        return new OnboardingResult(true);
    }

    /// <summary><c>SetRejectedState</c>: a legacy rejection the live account's <c>users.State</c>
    /// never recorded.</summary>
    internal sealed record PendingCopy(ConsentReviewInfo Review, bool CopyRow, bool SetRejectedState);

    /// <summary>Tombstones included for the row copy; only live accounts for the state.</summary>
    public async Task<IReadOnlyList<PendingCopy>> GetUncopiedLegacyReviewsAsync(CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        var pending = new List<PendingCopy>();
        foreach (var info in (await users.GetAllRawUserInfosAsync(ct)).OrderBy(u => u.CreatedAt))
        {
            if (LegacyReview(info) is not { } legacy)
                continue;
            var copyRow = !copied.Contains(info.Id);
            var setState = legacy.IsRejected && !info.IsTombstone && info.State != UserState.Rejected;
            if (copyRow || setState)
                pending.Add(new PendingCopy(legacy, copyRow, setState));
        }
        return pending;
    }

    public async Task<int> CopyLegacyReviewsAsync(CancellationToken ct = default)
    {
        var written = 0;
        foreach (var item in await GetUncopiedLegacyReviewsAsync(ct))
        {
            var wrote = item.CopyRow
                && await repo.TryInsertAsync(FromInfo(item.Review, clock.GetCurrentInstant()), ct);
            if (item.SetRejectedState)
                wrote |= (await userWrite.SetRejectedAsync(item.Review.UserId, ct)).Success;
            if (wrote)
                written++;
        }
        return written;
    }

    /// <summary>The only reader of the obsolete legacy fields; it goes when the data migration is retired. Null
    /// when the profile holds no review. The legacy enum maps by name.</summary>
    private static ConsentReviewInfo? LegacyReview(UserInfo? info)
    {
        if (info?.Profile is not { } p)
            return null;
#pragma warning disable HUM_PROFILE_REVIEW // read fallback and copy, until the data migration is retired
        if (p.ConsentCheckStatus is null && !p.IsApproved && p.RejectedAt is null)
            return null;
        return new ConsentReviewInfo(
            info.Id,
            p.ConsentCheckStatus switch
            {
                LegacyConsentCheckStatus.Pending => ConsentCheckStatus.Pending,
                LegacyConsentCheckStatus.Cleared => ConsentCheckStatus.Cleared,
                LegacyConsentCheckStatus.Flagged => ConsentCheckStatus.Flagged,
                _ => null,
            },
            p.ConsentCheckAt,
            p.ConsentCheckedByUserId,
            p.ConsentCheckNotes,
            p.IsApproved,
            p.RejectionReason,
            p.RejectedAt,
            p.RejectedByUserId);
#pragma warning restore HUM_PROFILE_REVIEW
    }

    private static ConsentReviewInfo ToInfo(ConsentReview r) => new(
        r.UserId, r.Status, r.CheckedAt, r.CheckedByUserId, r.Notes,
        r.IsApproved, r.RejectionReason, r.RejectedAt, r.RejectedByUserId);

    private static ConsentReview FromInfo(ConsentReviewInfo i, Instant now) => new()
    {
        UserId = i.UserId,
        Status = i.Status,
        CheckedAt = i.CheckedAt,
        CheckedByUserId = i.CheckedByUserId,
        Notes = i.Notes,
        IsApproved = i.IsApproved,
        RejectionReason = i.RejectionReason,
        RejectedAt = i.RejectedAt,
        RejectedByUserId = i.RejectedByUserId,
        UpdatedAt = now,
    };

    /// <summary>Rows of every id in the chain. Legacy profile values are still exported by Users' profile
    /// slices.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var rows = await repo.GetByUserIdsAsync(info?.AllUserIds ?? [userId], ct);
        object? data = rows.Count == 0
            ? null
            : rows.OrderBy(r => r.UserId == (info?.Id ?? userId) ? 0 : 1).Select(r => new
            {
                r.UserId,
                Status = r.Status?.ToString(),
                CheckedAt = r.CheckedAt.ToIso8601(),
                r.Notes,
                r.IsApproved,
                r.RejectionReason,
                RejectedAt = r.RejectedAt.ToIso8601(),
            }).ToList();
        return [new UserDataSlice(ConsentReviewSlice, data)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ConsentReviewSlice] =
                "Retained: the consent-check status, approval flag, review and rejection dates and the " +
                "reviewers' ids are the record of how the association admitted or rejected the member " +
                "(GDPR Art. 17(3)(e) legal claims). The Coordinator's notes and the rejection reason " +
                "are erased.",
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => repo.EraseFreeTextAsync(userId, ct);
}

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
/// The Consent Coordinator review Consent owns (<c>consent_reviews</c>): the reads behind
/// <see cref="IConsentService"/>, the <see cref="IConsentWrite"/> annotations and the Article 15/17
/// slice. A member without a row has not been reviewed.
/// </summary>
/// <remarks>
/// No <c>IUserMerge</c>: rows stay keyed to the id they were written for and reads resolve a
/// merged-away id forward, so the survivor's own review stands — as it did when these were
/// profile columns, which the profile fold never moved.
/// </remarks>
internal sealed class ConsentReviewService(
    IConsentReviewRepository repo,
    IUserService users,
    IClock clock) : IConsentWrite, IUserDataContributor
{
    internal const string ConsentReviewSlice = "ConsentReview";

    // ─── Reads ───────────────────────────────────────────────────────────────────

    public async Task<ConsentReviewInfo> GetReviewAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var id = info?.Id ?? userId;
        var row = await repo.GetAsync(id, ct);
        return row is not null ? ToInfo(row) : ConsentReviewInfo.None(id);
    }

    public async Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetReviewsAsync(CancellationToken ct = default) =>
        (await repo.GetAllAsync(ct)).ToDictionary(r => r.UserId, ToInfo);

    // ─── IConsentWrite ───────────────────────────────────────────────────────────

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

    // The write is keyed to the id asked with, as the profile was.
    private async Task<OnboardingResult> MutateAsync(
        Guid userId, Func<ConsentReview, Instant, string?> apply, CancellationToken ct)
    {
        var info = await users.GetRawUserInfoAsync(userId, ct);
        if (info?.Profile is null)
            return new OnboardingResult(false, "NotFound");

        var now = clock.GetCurrentInstant();
        var row = await repo.GetAsync(userId, ct) ?? FromInfo(ConsentReviewInfo.None(userId), now);
        var error = apply(row, now);
        if (error is not null)
            return new OnboardingResult(false, error);

        row.UpdatedAt = now;
        await repo.UpsertAsync(row, ct);
        return new OnboardingResult(true);
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

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    /// <summary>The rows of this human's own id and every merged-away id (rows stay keyed to the
    /// id they were written for).</summary>
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

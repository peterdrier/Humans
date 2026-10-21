using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// The member payment details Finance owns (<c>finance_payment_details</c>): the IBAN reads and
/// writes other sections make, the admin reveal, the Article 15/17 slice and the account-merge fold.
/// </summary>
internal sealed class PaymentDetailsService(
    IPaymentDetailsRepository repo,
    IUserService users,
    IAuditLogService audit,
    IClock clock) : IPaymentDetailsManagement, IFinanceWrite, IUserDataContributor, IUserMerge
{
    internal const string PaymentDetailsSlice = "PaymentDetails";

    public async Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        var row = await repo.GetAsync(info?.Id ?? userId, ct);
        return row?.Iban;
    }

    public async Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        if (await users.GetUserInfoAsync(userId, ct) is null) return false;
        var normalized = string.IsNullOrWhiteSpace(iban) ? null : IbanValidator.Normalize(iban);
        await repo.UpsertAsync(userId, normalized, clock.GetCurrentInstant(), ct);
        return true;
    }

    public async Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var iban = await GetIbanAsync(userId, ct);
        if (iban is null) return null;

        // Entity type and wording unchanged from the Users screen this came from, so earlier
        // reveals and new ones read back together.
        await audit.LogAsync(
            AuditAction.IbanReveal, nameof(User), userId,
            $"Admin revealed IBAN for user {userId}", actorUserId);
        return iban;
    }

    // ─── IUserMerge ──────────────────────────────────────────────────────────────

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        // The survivor's IBAN wins.
        await repo.ReassignAsync(mergedFromUserId, mergedToUserId, now, ct);
    }

    // ─── IUserDataContributor ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var iban = await GetIbanAsync(userId, ct);
        return [new UserDataSlice(PaymentDetailsSlice, iban is null ? null : new { Iban = IbanFormatter.Mask(iban) })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [PaymentDetailsSlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Deletes the member's row. The legacy <c>profiles.Iban</c> is erased by Users'
    /// profile erasure, which still owns that column.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}

using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// Until <c>finance.iban-to-payment-details</c> has run, a member without a row reads their legacy
/// <c>profiles.Iban</c> through <see cref="UserInfo"/>.
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
        return row is not null ? row.Iban : LegacyIban(info);
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

        // Same entity type and wording as before, so old and new reveals read back together.
        await audit.LogAsync(
            AuditAction.IbanReveal, nameof(User), userId,
            $"Admin revealed IBAN for user {userId}", actorUserId);
        return iban;
    }

    public async Task<IReadOnlyList<LegacyIbanRow>> GetUncopiedLegacyIbansAsync(CancellationToken ct = default)
    {
        var copied = (await repo.GetAllAsync(ct)).Select(r => r.UserId).ToHashSet();
        return [.. (await users.GetAllUserInfosAsync(ct))
            .Where(u => !copied.Contains(u.Id))
            .Select(u => (User: u, Iban: LegacyIban(u)))
            .Where(x => x.Iban is not null)
            .OrderBy(x => x.User.CreatedAt)
            .Select(x => new LegacyIbanRow(x.User.Id, x.User.BurnerName, x.Iban!))];
    }

    public async Task<int> CopyLegacyIbansAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var written = 0;
        foreach (var row in await GetUncopiedLegacyIbansAsync(ct))
        {
            if (await repo.TryInsertAsync(row.UserId, row.Iban, now, ct))
                written++;
        }
        return written;
    }

    /// <summary>The one reader of the obsolete field. Delete with the migration once production reports it done.</summary>
    private static string? LegacyIban(UserInfo? info)
    {
#pragma warning disable HUM_PROFILE_IBAN // the fallback and copy the field is kept for (plan §3.1)
        var iban = info?.Profile?.Iban;
#pragma warning restore HUM_PROFILE_IBAN
        return string.IsNullOrWhiteSpace(iban) ? null : iban;
    }

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
        // The survivor's IBAN wins. One still only on its legacy profile counts as its own, so it is
        // copied first rather than overwritten by the source's row.
        if (await repo.GetAsync(mergedToUserId, ct) is null
            && LegacyIban(await users.GetRawUserInfoAsync(mergedToUserId, ct)) is { } legacy)
        {
            await repo.TryInsertAsync(mergedToUserId, legacy, now, ct);
        }
        await repo.ReassignAsync(mergedFromUserId, mergedToUserId, now, ct);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var iban = await GetIbanAsync(userId, ct);
        return [new UserDataSlice(PaymentDetailsSlice, iban is null ? null : new { Iban = IbanFormatter.Mask(iban) })];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [PaymentDetailsSlice] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Users' profile erasure still owns and erases the legacy <c>profiles.Iban</c>.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        await repo.DeleteAsync(userId, ct);
}

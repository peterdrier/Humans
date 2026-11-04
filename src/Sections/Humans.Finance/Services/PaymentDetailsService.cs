using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

internal sealed class PaymentDetailsService(
    IPaymentDetailsRepository repo,
    IUserService users,
    IAuditLogService audit,
    IClock clock) : IPaymentDetailsManagement, IUserDataContributor, IUserMerge
{
    internal const string PaymentDetailsSlice = "PaymentDetails";

    public async Task<string?> GetIbanAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        return await ReadIbanAsync(info?.Id ?? userId, ct);
    }

    private async Task<string?> ReadIbanAsync(Guid userId, CancellationToken ct)
    {
        var row = await repo.GetAsync(userId, ct);
        return row?.Iban;
    }

    public async Task<bool> SetIbanAsync(Guid userId, string? iban, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        if (info is null) return false;
        var normalized = string.IsNullOrWhiteSpace(iban) ? null : IbanValidator.Normalize(iban);
        await repo.UpsertAsync(info.Id, normalized, clock.GetCurrentInstant(), ct);
        return true;
    }

    public async Task<string?> RevealIbanAsync(Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        userId = info?.Id ?? userId;
        var iban = await ReadIbanAsync(userId, ct);
        if (iban is null) return null;

        // Entity type and wording are persisted: unchanged, so every IbanReveal row reads back together.
        await audit.LogAsync(
            FinanceAuditActions.IbanReveal, AuditSubjectTypes.User, userId,
            $"Admin revealed IBAN for user {userId}", actorUserId);
        return iban;
    }

    public async Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        _ = actorUserId;
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

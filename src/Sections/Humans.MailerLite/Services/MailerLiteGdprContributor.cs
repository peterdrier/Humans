using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services;

/// <summary>
/// Export contributes nothing: subscriber state mirrors what other sections already export. Erasure deletes the
/// subscribers under the exact raw account's addresses, never a merge-resolved survivor's.
/// </summary>
internal sealed class MailerLiteGdprContributor(
    IMailerLiteService mailerLiteService,
    IUserService userService) : IApplicationService, IUserDataContributor
{
    /// <summary>Erasure only, so the section has a key to declare; never appears in an export.</summary>
    internal const string MailerLiteSubscriber = "MailerLiteSubscriber";

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MailerLiteSubscriber] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Every address the member owns, not just the primary: a subscriber often sits under a non-primary verified
    /// address, and once the local email rows are gone nothing could find it. A 404 counts as success, so a retry
    /// is safe.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await userService.GetRawUserInfoAsync(userId, ct);
        if (info is null) return;
        var addresses = new HashSet<string>(
            info.UserEmails.Where(email => email.IsVerified).Select(email => email.Email),
            StringComparer.OrdinalIgnoreCase);

        // A tombstone's vestigial identity address may now belong to the living survivor.
        if (!info.IsTombstone && info.Email is { } primary)
            addresses.Add(primary);

        // Import matching treats these domains as equivalent, but MailerLite deletion targets an exact address.
        foreach (var address in addresses.ToList())
        {
            var normalized = EmailNormalization.NormalizeForComparison(address);
            const string gmailSuffix = "@gmail.com";
            if (!normalized.EndsWith(gmailSuffix, StringComparison.Ordinal))
                continue;

            addresses.Add(normalized);
            addresses.Add(string.Concat(normalized.AsSpan(0, normalized.Length - gmailSuffix.Length), "@googlemail.com"));
        }

        foreach (var address in addresses)
            await mailerLiteService.DeleteSubscriberAsync(address, ct);
    }
}

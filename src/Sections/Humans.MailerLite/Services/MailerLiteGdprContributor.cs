using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services;

/// <summary>
/// Export contributes nothing: subscriber state mirrors what other sections already export. Erasure deletes the
/// subscriber. A separate class because <c>MailerLiteClient</c> is a Singleton and resolving emails needs the
/// Scoped <see cref="IUserEmailService"/>.
/// </summary>
internal sealed class MailerLiteGdprContributor(
    IMailerLiteService mailerLiteService,
    IUserEmailService userEmailService) : IApplicationService, IUserDataContributor
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
        var addresses = new HashSet<string>(
            await userEmailService.GetVerifiedEmailsForUserAsync(userId, ct),
            StringComparer.OrdinalIgnoreCase);

        if (await userEmailService.GetPrimaryEmailAsync(userId, ct) is { } primary)
            addresses.Add(primary);

        foreach (var address in addresses)
            await mailerLiteService.DeleteSubscriberAsync(address, ct);
    }
}

using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.MailerLite.Services;

/// <summary>
/// GDPR fan-out contributor for a section that owns no user-scoped tables (design-rules
/// §8a) but does hold per-person state in a third-party system: MailerLite subscriber
/// membership. Article 15 export contributes nothing — the subscriber list mirrors state
/// already exported by the sections that generate it (marketing opt-in lives on
/// <c>UserInfo.MarketingOptedOut</c>), not new personal data — so
/// <see cref="ContributeForUserAsync"/> always returns an empty list. Article 17 erasure
/// deletes the person's MailerLite subscriber outright (nobodies-collective/Humans#853).
///
/// <para>
/// Separate class rather than adding this role to <see cref="MailerLiteClient"/>: that
/// class is a Singleton (design §15 — it holds its own subscriber/group cache), so its
/// dependencies must all be Singleton-safe, and resolving a user's email needs the Scoped
/// <see cref="IUserEmailService"/>.
/// </para>
/// </summary>
internal sealed class MailerLiteGdprContributor(
    IMailerLiteService mailerLiteService,
    IUserEmailService userEmailService) : IApplicationService, IUserDataContributor
{
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.MailerLiteSubscriber] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Deletes the subscriber under <em>every</em> address the human owns, not just the
    /// current notification target. A subscriber routinely sits under a non-primary verified
    /// address — the "Frank pattern" the audience debug screen exists to explain
    /// (<c>Docs/features/audience-debug-screen.md</c>) — and erasing only the primary would
    /// leave that subscriber and its group memberships at the processor after the local
    /// <c>user_emails</c> rows are gone, with nothing left to find it by.
    ///
    /// <para>
    /// The primary is included explicitly rather than assumed to be in the verified set, and
    /// each delete is independently idempotent (an absent subscriber is a 404, which the
    /// client treats as success), so the whole method stays safe to retry.
    /// </para>
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

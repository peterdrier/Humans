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
    /// Resolves the same notification-target email the audience sync job matches
    /// subscribers against (<see cref="IUserEmailService.GetPrimaryEmailAsync"/>) and
    /// deletes that subscriber. No email on file means no subscriber could exist — nothing
    /// to erase.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var email = await userEmailService.GetPrimaryEmailAsync(userId, ct);
        if (email is null)
            return;

        await mailerLiteService.DeleteSubscriberAsync(email, ct);
    }
}

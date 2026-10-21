using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Email;

/// <summary>
/// Email's data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves Email's own
/// service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string CommunicationPreferencesKey = "email.communication-preferences";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(CommunicationPreferencesKey, "Email",
            "Copy every communication-preference row from Users' communication_preferences into email_communication_preferences",
            "CommunicationPreferenceService's legacy fallback, its cleared rows, UserInfo.CommunicationPreferences and LegacyMessageCategory; then a drop PR for communication_preferences",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Category", "Opted out", "Inbox", "Source", "Updated at", "Subscribed at"],
            [.. (await sp.GetRequiredService<ICommunicationPreferenceManagement>().GetUncopiedLegacyPreferencesAsync(ct))
                .Select(u => (IReadOnlyList<string>)
                [
                    u.UserId.ToString(), u.Preference.Category.ToString(),
                    u.Preference.OptedOut ? "yes" : "no", u.Preference.InboxEnabled ? "on" : "off",
                    u.Preference.UpdateSource,
                    u.Preference.UpdatedAt.ToIso8601(),
                    u.Preference.SubscribedAt.ToIso8601() ?? string.Empty,
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<ICommunicationPreferenceManagement>().CopyLegacyPreferencesAsync(ct);
}

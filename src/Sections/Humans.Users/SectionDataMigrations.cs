using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>
/// Users' data migrations, run from <c>/Debug/DataMigrations</c>. Each one resolves Users' own
/// services, so the copy stays inside the section; the Debug host only lists, applies and
/// audits the run. The remaining copy replaces the Temp screen that used to live at
/// <c>/Profile/Admin/EmailProblems/BackfillLegacyEmails</c>.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    private const string Section = "Users";

    internal const string LegacyIdentityEmailsKey = "users.legacy-identity-emails";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(LegacyIdentityEmailsKey, Section,
            "Create a verified UserEmail row from the legacy User.Email Identity column",
            "The legacy-column fallback in User.Email / UserInfo.Email",
            LegacyEmailsStatusAsync, ApplyLegacyEmailsAsync),
    ];

    // --- users.legacy-identity-emails ---

    private static async Task<DataMigrationStatus> LegacyEmailsStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Legacy User.Email"],
            [.. (await sp.GetRequiredService<IEmailProblemsService>().ScanAsync(ct)).Problems
                .Where(p => p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails && p.UserId is not null)
                .Select(p => (IReadOnlyList<string>)
                [
                    p.UserId!.Value.ToString(), p.Email ?? string.Empty,
                ])]);

    private static async Task<int> ApplyLegacyEmailsAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        // Audits LegacyIdentityEmailBackfilled per created row.
        (await sp.GetRequiredService<IEmailProblemsService>().BackfillLegacyIdentityEmailsAsync(actorUserId, ct)).Count;
}

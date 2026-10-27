using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>
/// Each migration resolves Users' own services, so the copy stays inside the section; the Debug
/// host only lists, applies and audits the run.
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

    private static async Task<DataMigrationStatus> LegacyEmailsStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Legacy User.Email"],
            [.. (await sp.GetRequiredService<IEmailProblemsService>().ScanAsync(ct)).Problems
                .Where(p => p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails && p.UserId is not null)
                .Select(p => (IReadOnlyList<string>)
                [
                    p.UserId!.Value.ToString(), p.Email ?? string.Empty,
                ])]);

    private static async Task<int> ApplyLegacyEmailsAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        (await sp.GetRequiredService<IEmailProblemsService>().BackfillLegacyIdentityEmailsAsync(actorUserId, ct)).Count;
}

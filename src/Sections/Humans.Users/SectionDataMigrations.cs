using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>
/// Users' data migrations, run from <c>/Debug/DataMigrations</c>. Each one resolves Users' own
/// services, so the copy stays inside the section; the Debug host only lists, applies and
/// audits the run. These replace the Temp screens that used to live at
/// <c>/Profile/Admin/Backfill</c> and <c>/Profile/Admin/EmailProblems/BackfillLegacyEmails</c>.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    private const string Section = "Users";

    internal const string StubProfilesKey = "users.stub-profiles";
    internal const string LegacyIdentityEmailsKey = "users.legacy-identity-emails";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(StubProfilesKey, Section,
            "Materialize a Stub Profile for every live user without one (nobodies-collective/Humans#635)",
            "The profile-less fallback branch of GET /Profile/{id}/Popover",
            StubProfilesStatusAsync, ApplyStubProfilesAsync),
        new(LegacyIdentityEmailsKey, Section,
            "Create a verified UserEmail row from the legacy User.Email Identity column",
            "The legacy-column fallback in User.Email / UserInfo.Email",
            LegacyEmailsStatusAsync, ApplyLegacyEmailsAsync),
    ];

    // --- users.stub-profiles ---

    private static async Task<IReadOnlyList<UserInfo>> UsersMissingProfileAsync(IServiceProvider sp, CancellationToken ct) =>
        // Tombstones are excluded: EnsureStubProfileAsync refuses them, so counting them would
        // keep the migration pending forever.
        [.. (await sp.GetRequiredService<IUserManagement>().GetAllUserInfosAsync(ct))
            .Where(u => u.Profile is null && !u.IsTombstone)
            .OrderByDescending(u => u.CreatedAt)];

    private static async Task<DataMigrationStatus> StubProfilesStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Email", "Display name", "Created", "Source"],
            [.. (await UsersMissingProfileAsync(sp, ct)).Select(u => (IReadOnlyList<string>)
            [
                u.Id.ToString(), u.Email ?? string.Empty, u.BurnerName,
                u.CreatedAt.ToInvariantTimestamp(), u.ContactSource?.ToString() ?? string.Empty,
            ])]);

    private static async Task<int> ApplyStubProfilesAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct)
    {
        var users = sp.GetRequiredService<IUserManagement>();
        var created = 0;
        foreach (var user in await UsersMissingProfileAsync(sp, ct))
        {
            // Idempotent — UserService takes a per-userId lock around the get/add pair, and
            // audits StubProfileBackfilled for each profile it creates.
            if (await users.EnsureStubProfileAsync(user.Id, ct: ct, actorUserId: actorUserId))
                created++;
        }
        return created;
    }

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

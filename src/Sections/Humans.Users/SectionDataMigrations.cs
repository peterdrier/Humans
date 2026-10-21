using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users;

/// <summary>
/// Users' data migrations, run from <c>/Debug/DataMigrations</c>. Each one resolves Users' own
/// services, so the copy stays inside the section; the Debug host only lists, applies and
/// audits the run. These four replace the Temp screens that used to live at
/// <c>/Profile/Admin/Backfill</c>, <c>/Profile/Admin/NameBackfill</c>,
/// <c>/Profile/Admin/PictureMigration</c> and <c>/Profile/Admin/EmailProblems/BackfillLegacyEmails</c>.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    private const string Section = "Users";

    internal const string StubProfilesKey = "users.stub-profiles";
    internal const string NamesOntoUserKey = "users.names-onto-user";
    internal const string PicturesToFilesystemKey = "users.profile-pictures-to-filesystem";
    internal const string LegacyIdentityEmailsKey = "users.legacy-identity-emails";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(StubProfilesKey, Section,
            "Materialize a Stub Profile for every live user without one (nobodies-collective/Humans#635)",
            "The profile-less fallback branch of GET /Profile/{id}/Popover",
            StubProfilesStatusAsync, ApplyStubProfilesAsync),
        new(NamesOntoUserKey, Section,
            "Copy BurnerName and legal name from Profile onto the User row (nobodies-collective/Humans#1097)",
            "Profile.BurnerName/FirstName/LastName as name sources (nobodies-collective/Humans#1098)",
            NamesOntoUserStatusAsync, ApplyNamesOntoUserAsync),
        new(PicturesToFilesystemKey, Section,
            "Move DB-recorded custom profile pictures onto the filesystem (nobodies-collective/Humans#702)",
            "Profile.ProfilePictureContentType as a migrate-on-read trigger",
            PicturesStatusAsync, ApplyPicturesAsync),
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

    // --- users.names-onto-user ---

    private static async Task<DataMigrationStatus> NamesOntoUserStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Email", "Status", "Burner name", "Legal name", "Missing"],
            [.. (await sp.GetRequiredService<IUserNameSyncService>().GetUnsyncedAsync(ct)).Select(r => (IReadOnlyList<string>)
            [
                r.UserId.ToString(), r.Email, r.State.ToString(), r.ProfileBurnerName, r.ProfileLegalName,
                (r.BurnerNameMissing, r.LegalNameMissing) switch
                {
                    (true, true) => "burner + legal",
                    (true, false) => "burner",
                    _ => "legal",
                },
            ])]);

    private static Task<int> ApplyNamesOntoUserAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        // Audits UserNamesBackfilled per saved member.
        sp.GetRequiredService<IUserNameSyncService>().SyncAllAsync(actorUserId, ct);

    // --- users.profile-pictures-to-filesystem ---

    private static async Task<DataMigrationStatus> PicturesStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["Profile", "User", "Display name", "Content type", "Updated"],
            [.. (await sp.GetRequiredService<IProfilePictureService>().GetProfilePictureMigrationSnapshotAsync(ct))
                .DbOnlyRows.Select(r => (IReadOnlyList<string>)
                [
                    r.ProfileId.ToString(), r.UserId.ToString(), r.DisplayName, r.ContentType,
                    r.UpdatedAt.ToInvariantTimestamp(),
                ])]);

    private static async Task<int> ApplyPicturesAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct)
    {
        var pictures = sp.GetRequiredService<IProfilePictureService>();
        var logger = sp.GetRequiredService<ILogger<SectionDataMigrations>>();
        var snapshot = await pictures.GetProfilePictureMigrationSnapshotAsync(ct);

        // GetProfilePictureAsync's filesystem-save side effect IS the migration.
        var migrated = 0;
        foreach (var row in snapshot.DbOnlyRows)
        {
            if (await pictures.GetProfilePictureAsync(row.ProfileId, ct) is not null)
            {
                migrated++;
            }
            else
            {
                logger.LogWarning(
                    "Profile-picture migration: GetProfilePictureAsync returned null for DB-only profile {ProfileId} (userId {UserId}); row skipped",
                    row.ProfileId, row.UserId);
            }
        }
        return migrated;
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

using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.GoogleIntegration;

/// <summary>
/// GoogleIntegration's data migrations, run from <c>/Debug/DataMigrations</c>. The copy resolves
/// GoogleIntegration's own service; the Debug host lists, applies and audits the run.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string WorkspaceIdentitiesKey = "google.workspace-identities";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(WorkspaceIdentitiesKey, "GoogleIntegration",
            "Copy each member's Google identity (user_emails.IsGoogle and GoogleEmailStatus) into google_workspace_identities",
            "WorkspaceIdentityService's legacy fallback, UserEmailInfo.IsGoogle/GoogleEmailStatus and LegacyGoogleEmailStatus; then a drop PR for user_emails.IsGoogle and user_emails.GoogleEmailStatus",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Burner name", "Google identity", "Status"],
            [.. (await sp.GetRequiredService<IWorkspaceIdentityManagement>().GetUncopiedLegacyIdentitiesAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.UserId.ToString(), r.BurnerName, r.Email, r.Status.ToString()])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IWorkspaceIdentityManagement>().CopyLegacyIdentitiesAsync(ct);
}

using Humans.Auth.Contracts;
using Humans.Auth.Services;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Auth;

/// <summary>
/// Rewrites role_assignments rows stored under a role's former name to its current name. Until it has run, Auth
/// reads former names as the current role (<c>RoleCatalog.Canonical</c>), so nobody loses a role at deploy.
/// </summary>
internal sealed class SectionDataMigrations : ISectionDataMigrations
{
    internal const string RoleRenamesKey = "auth.role-renames";

    public IEnumerable<DataMigration> Migrations() =>
    [
        new(RoleRenamesKey, "Auth",
            "Rewrite role assignments stored under a former role name (HumanAdmin, CampAdmin, TicketAdmin, NoInfoAdmin, VolunteerCoordinator, EETeamAdmin) to the current name",
            "RoleDefinition.FormerNames on each renamed role and the former-name reads in RoleAssignmentService",
            StatusAsync, ApplyAsync),
    ];

    private static async Task<DataMigrationStatus> StatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Stored role", "Current role", "Valid to"],
            [.. (await sp.GetRequiredService<RoleAssignmentService>().GetRowsUnderFormerNamesAsync(ct))
                .Select(r => (IReadOnlyList<string>)
                [
                    r.UserId.ToString(), r.StoredName, r.CurrentName, r.ValidTo.ToIso8601() ?? "open",
                ])]);

    private static Task<int> ApplyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<RoleAssignmentService>().RewriteFormerNamesAsync(ct);
}

using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Tests.Architecture.Ratchet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The cross-section role invariants over the real discovered <see cref="ISectionRoles"/> contributions: naming,
/// the admin-shell gate derived from them, and that no name a row was ever stored under has been lost.
/// </summary>
public class SectionRolesTests
{
    private static readonly IReadOnlyList<ISectionRoles> Contributors =
        SectionDiscoveryExtensions.DiscoverImplementations<ISectionRoles>();

    private static readonly RoleCatalog Catalog = new(Contributors);

    /// <summary>
    /// Every name a role_assignments row has ever been stored under, on QA or production. Nothing is ever removed
    /// from this list: a rename adds the new name here and the old one to the role's <c>FormerNames</c>, so the
    /// stored rows keep resolving until the <c>auth.role-renames</c> data migration rewrites them.
    /// </summary>
    private static readonly string[] ShippedStoredNames =
    [
        "Admin", "Board",
        "UsersAdmin", "HumanAdmin",
        "TeamsAdmin",
        "CampsAdmin", "CampAdmin",
        "TicketsAdmin", "TicketAdmin",
        "ConsentCoordinator",
        "EventsAdmin",
        "FeedbackAdmin",
        "FinanceAdmin",
        "StoreAdmin",
        "CantinaAdmin",
        "RideshareAdmin",
        "EarlyEntryAdmin", "EETeamAdmin",
        "ShiftsAdmin", "VolunteerCoordinator",
        "ShiftsNoInfo", "NoInfoAdmin",
        "GrantsAdmin",
        "SurveysAdmin",
    ];

    [HumansFact]
    public void Every_name_ever_stored_still_resolves_to_a_catalogued_role()
    {
        var lost = ShippedStoredNames.Where(n => !Catalog.Contains(Catalog.Canonical(n))).ToList();

        lost.Should().BeEmpty(
            because: "a stored role name that no longer resolves silently strips the role from everyone holding it; "
                   + "rename through FormerNames instead");
    }

    [HumansFact]
    public void Every_current_role_is_listed_among_the_shipped_stored_names()
    {
        var unlisted = Catalog.Roles.Select(r => r.Name).Except(ShippedStoredNames, StringComparer.Ordinal).ToList();

        unlisted.Should().BeEmpty(because: "a new role's name joins ShippedStoredNames in the PR that ships it");
    }

    [HumansFact]
    public void Section_roles_carry_their_section_name()
    {
        // Admin and Board are the two system-wide roles; Auth declares them without a prefix.
        var offenders = Contributors
            .SelectMany(c => c.Roles.Select(r => (Section: SectionDiscoveryExtensions.SectionName(c.GetType().Assembly), r.Name)))
            .Where(x => x.Name is not (RoleNames.Admin or RoleNames.Board))
            .Where(x => !x.Name.StartsWith(x.Section, StringComparison.Ordinal))
            .ToList();

        offenders.Should().BeEmpty(because: "a role is named <Section><Role> so its owner is readable from the name");
    }

    [HumansFact]
    public void Admin_and_Board_are_declared_once_by_Auth()
    {
        var system = Contributors
            .SelectMany(c => c.Roles.Select(r => (Section: SectionDiscoveryExtensions.SectionName(c.GetType().Assembly), r.Name)))
            .Where(x => x.Name is RoleNames.Admin or RoleNames.Board)
            .ToList();

        system.Should().BeEquivalentTo([("Auth", RoleNames.Admin), ("Auth", RoleNames.Board)]);
    }

    [HumansFact]
    public void AnyAdminRole_admits_exactly_the_roles_that_open_the_admin_shell()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var contributor in Contributors)
            services.AddSingleton(contributor);
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));
        services.AddHumansAuthorizationPolicies();
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(PolicyNames.AnyAdminRole);
        var allowed = policy!.Requirements.Should().ContainSingle().Which
            .Should().BeOfType<RolesAuthorizationRequirement>().Which.AllowedRoles;

        allowed.Should().BeEquivalentTo(Catalog.Roles.Where(r => r.OpensAdminShell).Select(r => r.Name));
        allowed.Should().NotContain("EarlyEntryAdmin", because: "Early Entry admins work from their own pages, not the shell");
        allowed.Should().Contain([RoleNames.Admin, RoleNames.Board]);
    }

    private static readonly Regex Heading = new(@"^##\s+As\s+an?\s.*?\((?<paren>[^()]*)\)\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The Guide scopes a section by the titles in its heading parentheticals; the Guide's own tests check them
    /// against a fixture catalog, this one against the roles the sections really declare.
    /// </summary>
    [HumansFact]
    public void Every_guide_heading_parenthetical_names_a_catalogued_title()
    {
        var unknown = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RatchetTestRunner.LocateRepoRoot(), "docs", "guide"), "*.md"))
        {
            foreach (var line in File.ReadLines(file))
            {
                var match = Heading.Match(line.TrimEnd('\r'));
                if (!match.Success)
                    continue;

                // "## As a [Coordinator](Glossary.md#coordinator)": a link destination, not a role scope.
                var open = match.Groups["paren"].Index - 1;
                if (open >= 1 && line[open - 1] == ']')
                    continue;

                foreach (var token in match.Groups["paren"].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!string.Equals(token, "Camp Lead", StringComparison.OrdinalIgnoreCase)
                        && Catalog.FindByTitle(token) is null)
                    {
                        unknown.Add($"{Path.GetFileName(file)}: ({token})");
                    }
                }
            }
        }

        unknown.Should().BeEmpty(because: "a parenthetical that names no role title reaches nobody");
    }
}

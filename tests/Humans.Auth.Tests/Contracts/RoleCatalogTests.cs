using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests.Contracts;

public sealed class RoleCatalogTests
{
    private static RoleDefinition Role(string name, string title, params string[] former) =>
        new(name, title, "", "", FormerNames: former);

    [HumansFact]
    public void Duplicate_name_across_sections_throws()
    {
        var act = () => new RoleCatalog([Role("XAdmin", "X Admin"), Role("XAdmin", "Other")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*XAdmin*");
    }

    [HumansFact]
    public void Duplicate_title_throws()
    {
        // Guide parentheticals resolve by title, so two roles sharing one would be ambiguous.
        var act = () => new RoleCatalog([Role("XAdmin", "Same"), Role("YAdmin", "same")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Same*");
    }

    [HumansFact]
    public void Former_name_that_still_names_a_role_throws()
    {
        var act = () => new RoleCatalog([Role("XAdmin", "X Admin", "YAdmin"), Role("YAdmin", "Y Admin")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*YAdmin*");
    }

    [HumansFact]
    public void Former_name_shared_by_two_roles_throws()
    {
        var act = () => new RoleCatalog([Role("XAdmin", "X Admin", "Old"), Role("YAdmin", "Y Admin", "Old")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Old*");
    }

    [HumansFact]
    public void Canonical_maps_former_names_and_passes_unknown_names_through()
    {
        var catalog = new RoleCatalog([Role("XAdmin", "X Admin", "OldX")]);

        catalog.Canonical("OldX").Should().Be("XAdmin");
        catalog.Canonical("XAdmin").Should().Be("XAdmin");
        catalog.Canonical("Nobody").Should().Be("Nobody");
    }

    [HumansFact]
    public void IsFormerName_is_true_only_for_a_former_name()
    {
        var catalog = new RoleCatalog([Role("XAdmin", "X Admin", "OldX")]);

        catalog.IsFormerName("OldX").Should().BeTrue();
        catalog.IsFormerName("XAdmin").Should().BeFalse();
        catalog.IsFormerName("Nobody").Should().BeFalse();
    }

    [HumansFact]
    public void StoredNames_lists_current_name_first_then_former_ones()
    {
        var catalog = new RoleCatalog([Role("XAdmin", "X Admin", "OldX", "OlderX"), Role("YAdmin", "Y Admin")]);

        catalog.StoredNames("XAdmin").Should().Equal("XAdmin", "OldX", "OlderX");
        catalog.StoredNames("YAdmin").Should().Equal("YAdmin");
        catalog.StoredNames("Nobody").Should().Equal("Nobody");
        catalog.AllFormerNames.Should().BeEquivalentTo(["OldX", "OlderX"]);
    }

    [HumansFact]
    public void FindByTitle_ignores_case_and_surrounding_whitespace()
    {
        var catalog = new RoleCatalog([Role("XAdmin", "X Admin")]);

        catalog.FindByTitle(" x admin ")!.Name.Should().Be("XAdmin");
        catalog.FindByTitle("XAdmin").Should().BeNull("names are not titles");
        catalog.Title("XAdmin").Should().Be("X Admin");
        catalog.Title("Nobody").Should().Be("Nobody");
    }

    [HumansFact]
    public void Roles_keep_contribution_order()
    {
        var catalog = new RoleCatalog(new ISectionRoles[]
        {
            new Contributor(Role("ZAdmin", "Z Admin")),
            new Contributor(Role("AAdmin", "A Admin"), Role("MAdmin", "M Admin")),
        });

        catalog.Roles.Select(r => r.Name).Should().Equal("ZAdmin", "AAdmin", "MAdmin");
        catalog.Contains("MAdmin").Should().BeTrue();
        catalog.Find("Nobody").Should().BeNull();
    }

    private sealed class Contributor(params RoleDefinition[] roles) : ISectionRoles
    {
        public IReadOnlyList<RoleDefinition> Roles { get; } = roles;
    }
}

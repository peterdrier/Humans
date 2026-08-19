using AwesomeAssertions;

namespace Humans.Gdpr.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Gdpr
/// (nobodies-collective/Humans#866, G5). The section had no architecture test file
/// before the move — its G0 audit recorded the missing invariants doc as predicate 7's
/// only gap and left the shape untested — so these are new with the project.
/// </summary>
public class GdprArchitectureTests
{
    [HumansFact]
    public void OnlySectionIsPublic()
    {
        // "Public means Section, <Section>Resource or Contracts/" (design §15 step 5),
        // enforced at build time by HUM0034. Gdpr's whole outward surface lives on the
        // separate Humans.Gdpr.Contracts leaf, it owns no tables so there are no
        // migrations, it ships no resource set, and GuestDataController is internal
        // (HUM0034) — so Section is the only public type in the section assembly.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(["Humans.Gdpr.Section"]);
    }

    [HumansFact]
    public void SectionHasExactlyOneController()
    {
        // GuestDataController (added nobodies-collective/Humans#1091, moved from Shell's
        // GuestController with its route unchanged) only ever returns File()/Redirect —
        // no Views/ tree, no Sdk.Razor. /Profile/Me/DownloadData stays on Users'
        // ProfileController; moving it would be a URL change, out of scope here.
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().ContainSingle(t => t.Name == "GuestDataController");
    }
}

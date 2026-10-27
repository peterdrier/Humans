using AwesomeAssertions;

namespace Humans.Email.Tests.Services;

public sealed class EmailProjectReferenceTests
{
    /// <summary>
    /// A crosscut may not grow new edges into sections: these assertions stop a new reference
    /// arriving, not bless what is here. Asserted from the csproj's own &lt;ProjectReference&gt;
    /// items, not <c>Assembly.GetReferencedAssemblies()</c>: a const-only or unused reference
    /// emits no metadata reference and would pass that check silently.
    /// </summary>
    [HumansFact]
    public void EmailContracts_ReferencesOnlyBaseAndUsersContracts() =>
        ProjectReferencesOf("Humans.Email.Contracts").Should().BeSubsetOf(
            ["Humans.Base", "Humans.Users.Contracts"],
            because: "Email is a crosscut: its contracts leaf may lose section references, never gain one");

    [HumansFact]
    public void Email_ReferencesOnlyItsCurrentSectionContracts() =>
        ProjectReferencesOf("Humans.Email").Should().BeSubsetOf(
            [
                "Humans.AuditLog", "Humans.Base", "Humans.Campaigns", "Humans.Email.Contracts",
                "Humans.Gdpr", "Humans.Settings", "Humans.Tickets.Contracts", "Humans.Users.Contracts"
            ],
            because: "Email is a crosscut: it may lose section references, never gain one. "
                + "Tickets.Contracts (a Base-only leaf) arrived with "
                + "communication preferences (Users decomposition commit 14): the preferences panel "
                + "locks the Ticketing row through ITicketService.GetUserTicketHoldingsAsync");

    private static IEnumerable<string> ProjectReferencesOf(string projectName)
    {
        var csprojPath = Path.Combine(LocateRepoRoot(), "src", "Sections", projectName, projectName + ".csproj");
        var xml = System.Xml.Linq.XDocument.Load(csprojPath);
        return xml.Descendants("ProjectReference")
            .Select(e => ((string)e.Attribute("Include")!).Split('\\', '/')[^1])
            .Select(name => name[..^".csproj".Length])
            .Distinct(StringComparer.Ordinal);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (no Humans.slnx above " + AppContext.BaseDirectory + ").");
    }
}

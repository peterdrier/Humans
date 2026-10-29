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
    public void EmailContracts_ReferencesOnlyBaseAuditLogAndUsersContracts() =>
        ProjectReferencesOf("Humans.Email.Contracts").Should().BeSubsetOf(
            ["Humans.AuditLog", "Humans.Base", "Humans.Users.Contracts"],
            because: "Email is a crosscut: its contracts leaf may lose section references, never gain one (AuditLog is there for the audit action definitions it publishes)");

    [HumansFact]
    public void Email_ReferencesOnlyItsCurrentSectionContracts() =>
        ProjectReferencesOf("Humans.Email").Should().BeSubsetOf(
            [
                "Humans.AuditLog", "Humans.Base", "Humans.Email.Contracts",
                "Humans.Gdpr", "Humans.Settings", "Humans.Users.Contracts"
            ],
            because: "Email is a crosscut: it may lose section references, never gain one");

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

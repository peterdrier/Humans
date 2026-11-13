using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Web.ViewComponents;

namespace Humans.Web;

/// <summary>
/// Shell-owned dashboard chrome and help content for the Admin and Board frames.
/// Registered by name in <c>Program.cs</c>: section discovery walks section assemblies only.
/// </summary>
internal sealed class ShellChrome : ISectionChrome, ISectionHelp
{
    public IReadOnlyList<SectionHelpEntry> HelpEntries { get; } =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(ShellChrome).Assembly, "Admin", 30),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(ShellChrome).Assembly, "Board", 80),
    ];

    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, typeof(LoginMethodCardViewComponent), Weight: 50)];
}

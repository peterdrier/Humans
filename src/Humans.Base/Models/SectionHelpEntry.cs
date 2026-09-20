using System.Reflection;

namespace Humans.Base.Models;

/// <summary>
/// One section-help page's content: the long procedural Guide and the term Glossary the help
/// modal shows for <paramref name="Key"/>. Contributed by the section that owns the page
/// (<see cref="Humans.Base.Interfaces.ISectionHelp"/>), never declared in Base.
/// </summary>
/// <param name="Key">The key <c>&lt;vc:access-matrix section="..."&gt;</c> is called with. One
/// section may own several, so it is not the section name.</param>
/// <param name="Order">Position in the one flat glossary corpus the agent preload renders.
/// Explicit because contributions arrive in DI order and one section's entries are not
/// contiguous in that corpus. Spaced by ten so a new entry slots in without renumbering.</param>
/// <param name="Guide">The Guide tab's markdown, or null when the page has none.</param>
/// <param name="Glossary">The Glossary tab's markdown, or null when the page has none.</param>
public sealed record SectionHelpEntry(string Key, int Order, string? Guide, string? Glossary)
{
    /// <summary>
    /// The entry for <paramref name="key"/>, read from the contributing assembly's embedded
    /// <c>Docs/help/{key}.guide.md</c> and <c>Docs/help/{key}.glossary.md</c>. The markdown
    /// lives beside the section's other docs rather than inline in C#, so it is editable and
    /// reviewable as markdown; it is embedded rather than fetched because the help modal
    /// renders synchronously on every section landing page and must not depend on the
    /// network-backed doc readers. A file the section does not ship yields null for that tab.
    /// </summary>
    public static SectionHelpEntry FromEmbeddedMarkdown(Assembly assembly, string key, int order) =>
        new(key, order, Read(assembly, key, "guide"), Read(assembly, key, "glossary"));

    private static string? Read(Assembly assembly, string key, string kind)
    {
        var name = FormattableString.Invariant($"{assembly.GetName().Name}.Docs.help.{key}.{kind}.md");
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        // File framing only: every text file ends with a newline, the string literal this
        // replaced did not. Exactly one is removed so a body that genuinely ends blank survives.
        var text = reader.ReadToEnd().ReplaceLineEndings("\n");
        return text.EndsWith('\n') ? text[..^1] : text;
    }
}

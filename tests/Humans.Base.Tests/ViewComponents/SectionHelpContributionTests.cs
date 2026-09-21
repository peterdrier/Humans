using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Humans.Base.Tests.ViewComponents;

/// <summary>
/// The section-help widget resolves its Guide and Glossary out of the
/// <see cref="ISectionHelp"/> contributions DI discovered — Base holds no list of which
/// sections have help content, so what the widget can render is exactly what was contributed.
/// </summary>
public class SectionHelpContributionTests
{
    private sealed class Stub(params SectionHelpEntry[] entries) : ISectionHelp
    {
        public IReadOnlyList<SectionHelpEntry> HelpEntries => entries;
    }

    private static SectionHelpEntry Entry(string key) =>
        new(key, 10, $"## {key} guide", $"## {key} Glossary");

    [HumansFact]
    public void Resolves_help_from_any_contribution_not_a_list_Base_keeps()
    {
        var component = new AccessMatrixViewComponent([], [new Stub(Entry("First")), new Stub(Entry("Second"))]);

        foreach (var key in new[] { "First", "Second" })
        {
            var model = component.Invoke(key).Should().BeOfType<ViewViewComponentResult>()
                .Subject.ViewData!.Model.Should().BeOfType<SectionHelpViewModel>().Subject;
            model.GuideMarkdown.Should().Be($"## {key} guide");
            model.GlossaryMarkdown.Should().Be($"## {key} Glossary");
        }
    }

    /// <summary>A key nobody contributed — and with no access matrix either — renders nothing at all.</summary>
    [HumansFact]
    public void Renders_nothing_for_a_key_no_section_contributed()
    {
        new AccessMatrixViewComponent([], [new Stub(Entry("First"))])
            .Invoke("NotASection")
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    /// <summary>
    /// The key is matched ordinally, as the dictionaries this replaced were: the call site
    /// spells the key the contributing section declared.
    /// </summary>
    [HumansFact]
    public void Matches_the_key_ordinally()
    {
        new AccessMatrixViewComponent([], [new Stub(Entry("First"))])
            .Invoke("first")
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }
}

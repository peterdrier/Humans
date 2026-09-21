using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;

namespace Humans.Base.Tests.ViewComponents;

/// <summary>
/// The section-help widget resolves its matrix out of the <see cref="ISectionAccessMatrix"/>
/// contributions DI discovered — Base holds no list of which sections have one, so what the
/// widget can render is exactly what was contributed.
/// </summary>
public class AccessMatrixViewComponentTests
{
    private sealed class Stub(params AccessMatrixData[] matrices) : ISectionAccessMatrix
    {
        public IReadOnlyList<AccessMatrixData> AccessMatrices => matrices;
    }

    private static AccessMatrixData Data(string key, string name) => new()
    {
        Key = key,
        SectionName = name,
        Order = 10,
        Roles = ["Volunteer"],
        Features = [AccessMatrixFeature.Of("Do a thing", ("Volunteer", AccessLevel.Allowed))]
    };

    [HumansFact]
    public void Resolves_the_matrix_from_any_contribution_not_a_list_Base_keeps()
    {
        var component = new AccessMatrixViewComponent(
            [new Stub(Data("First", "First Page")), new Stub(Data("Second", "Second Page"))], []);

        foreach (var (key, name) in new[] { ("First", "First Page"), ("Second", "Second Page") })
        {
            var model = component.Invoke(key).Should().BeOfType<ViewViewComponentResult>()
                .Subject.ViewData!.Model.Should().BeOfType<SectionHelpViewModel>().Subject;
            model.SectionName.Should().Be(name);
            model.AccessMatrix!.Key.Should().Be(key);
        }
    }

    /// <summary>A key nobody contributed and that has no guide or glossary renders nothing at all.</summary>
    [HumansFact]
    public void Renders_nothing_for_a_key_no_section_contributed()
    {
        new AccessMatrixViewComponent([new Stub(Data("First", "First Page"))], [])
            .Invoke("NotASection")
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    /// <summary>
    /// The key is matched ordinally, as the dictionary this replaced was: the call site spells
    /// the key the contributing section declared.
    /// </summary>
    [HumansFact]
    public void Matches_the_key_ordinally()
    {
        new AccessMatrixViewComponent([new Stub(Data("First", "First Page"))], [])
            .Invoke("first")
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }
}

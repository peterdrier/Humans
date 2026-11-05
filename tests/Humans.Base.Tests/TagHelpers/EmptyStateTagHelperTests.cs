using AwesomeAssertions;
using Humans.Base.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Xunit;

namespace Humans.Base.Tests.TagHelpers;

public class EmptyStateTagHelperTests
{
    [HumansFact]
    public void Process_NoClassAttribute_RendersMutedCenteredParagraph()
    {
        var output = Run(new TagHelperAttributeList());

        output.TagName.Should().Be("p");
        output.TagMode.Should().Be(TagMode.StartTagAndEndTag);
        output.Attributes["class"].Value.Should().Be("text-muted text-center");
    }

    [HumansFact]
    public void Process_ClassAttribute_IsAppendedAfterBaseClasses()
    {
        var output = Run(new TagHelperAttributeList { { "class", " mb-0 small " } });

        output.Attributes["class"].Value.Should().Be("text-muted text-center mb-0 small");
    }

    [HumansFact]
    public void Process_BlankClassAttribute_AddsNoTrailingSpace()
    {
        var output = Run(new TagHelperAttributeList { { "class", "  " } });

        output.Attributes["class"].Value.Should().Be("text-muted text-center");
    }

    [HumansFact]
    public void Process_ChildContent_PassesThroughUntouched()
    {
        var output = Run(new TagHelperAttributeList(), "No <strong>rotas</strong> yet.");

        output.Content.GetContent().Should().Be("No <strong>rotas</strong> yet.");
    }

    private static TagHelperOutput Run(TagHelperAttributeList attributes, string content = "")
    {
        var context = new TagHelperContext(
            "empty-state", new TagHelperAttributeList(), new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput(
            "empty-state",
            attributes,
            (_, _) =>
            {
                var tagHelperContent = new DefaultTagHelperContent();
                tagHelperContent.SetHtmlContent(content);
                return Task.FromResult<TagHelperContent>(tagHelperContent);
            });
        output.Content.SetHtmlContent(content);

        new EmptyStateTagHelper().Process(context, output);
        return output;
    }
}

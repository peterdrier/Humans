using System.Text.Encodings.Web;
using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Models.Tables;
using Humans.Base.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Base.Tests.TagHelpers;

public class StatusBadgeTagHelperTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    private IStringLocalizer<SharedResource> Localizer => _services.GetRequiredService<IStringLocalizer<SharedResource>>();

    private TagHelperOutput Run(Enum? value, string? text = null, string? cssClass = null)
    {
        var attributes = new TagHelperAttributeList();
        if (cssClass is not null)
            attributes.Add("class", cssClass);
        var output = new TagHelperOutput("status-badge", attributes,
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        var context = new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id");
        new StatusBadgeTagHelper(Localizer) { Value = value, Text = text }.Process(context, output);
        return output;
    }

    [HumansFact]
    public void RendersBadgeSpan_WithMapClassAndEnumDisplay()
    {
        var output = Run(EmailOutboxStatus.Failed);

        output.TagName.Should().Be("span");
        output.TagMode.Should().Be(TagMode.StartTagAndEndTag);
        output.Attributes["class"].Value.Should().Be($"badge {EnumBadgeMap.For(EmailOutboxStatus.Failed)}");
        output.Content.GetContent().Should().Be(Localizer.EnumDisplay(EmailOutboxStatus.Failed));
    }

    [HumansFact]
    public void ExtraClass_IsAppendedAfterBadgeClasses()
    {
        var output = Run(EmailOutboxStatus.Sent, cssClass: " ms-1 ");

        output.Attributes["class"].Value.Should().Be($"badge {EnumBadgeMap.For(EmailOutboxStatus.Sent)} ms-1");
        output.Attributes.Count(a => string.Equals(a.Name, "class", StringComparison.Ordinal)).Should().Be(1);
    }

    [HumansFact]
    public void TextOverride_ReplacesEnumDisplay_AndIsEncoded()
    {
        var output = Run(EmailOutboxStatus.Queued, text: "<b>Set up</b>");

        using var writer = new StringWriter();
        output.Content.WriteTo(writer, HtmlEncoder.Default);
        writer.ToString().Should().Be("&lt;b&gt;Set up&lt;/b&gt;");
    }

    [HumansFact]
    public void LocalizerAttribute_SuppliesTheEnumDisplayInsteadOfSharedResource()
    {
        var sectionLocalizer = Substitute.For<IStringLocalizer>();
        sectionLocalizer["Enum_EmailOutboxStatus_Sent"].Returns(new LocalizedString("Enum_EmailOutboxStatus_Sent", "Enviado"));
        var output = new TagHelperOutput("status-badge", new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        var context = new TagHelperContext(new TagHelperAttributeList(), new Dictionary<object, object>(), "id");

        new StatusBadgeTagHelper(Localizer) { Value = EmailOutboxStatus.Sent, Localizer = sectionLocalizer }
            .Process(context, output);

        output.Content.GetContent().Should().Be("Enviado");
    }

    [HumansFact]
    public void NullValue_RendersNothing()
    {
        var output = Run(null);

        output.TagName.Should().BeNull();
        output.Content.GetContent().Should().BeEmpty();
    }
}

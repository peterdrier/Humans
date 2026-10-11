using System.Text.Encodings.Web;
using Humans.Base;
using Humans.Base.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Views;

public sealed class TabbedMarkdownDocumentsTests
{
    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingContent_RendersEncodedEmptyMessage(bool hasEmptyTranslation)
    {
        var model = new TabbedMarkdownDocumentsViewModel
        {
            Documents = hasEmptyTranslation ? [new("es", string.Empty)] : [],
            DefaultLanguage = "es",
            EmptyMessage = "Contenido no disponible. <retry>",
        };
        var rendered = await RenderAsync(model);

        Assert.Contains(HtmlEncoder.Default.Encode(model.EmptyMessage), rendered, StringComparison.Ordinal);
    }

    [HumansTheory]
    [InlineData("es", "Castellano (Rechtsverbindlich)")]
    [InlineData("en", "English")]
    public async Task LanguageTabs_LocalizeOnlyCanonicalLegalMarker(string language, string label)
    {
        var rendered = await RenderAsync(new TabbedMarkdownDocumentsViewModel
        {
            Documents = [new(language, string.Empty)],
            DefaultLanguage = language,
        });

        Assert.Contains(HtmlEncoder.Default.Encode(label), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("(Legal)", rendered, StringComparison.Ordinal);
    }

    private static async Task<string> RenderAsync(TabbedMarkdownDocumentsViewModel model)
    {
        var item = new RazorCompiledItemLoader().LoadItems(typeof(TabbedMarkdownDocumentsViewModel).Assembly)
            .Single(i => string.Equals(i.Identifier, "/Views/Shared/_TabbedMarkdownDocuments.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<TabbedMarkdownDocumentsViewModel>)Activator.CreateInstance(item.Type)!;
        item.Type.GetProperty("Html")!.SetValue(page, Substitute.For<IHtmlHelper<TabbedMarkdownDocumentsViewModel>>());
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer["Document_LegalLabel"].Returns(new LocalizedString("Document_LegalLabel", "Rechtsverbindlich"));
        item.Type.GetProperty("Localizer")!.SetValue(page, localizer);
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<TabbedMarkdownDocumentsViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model,
        };
        page.ViewContext = new ViewContext { Writer = writer, ViewData = data };
        page.ViewData = data;

        await page.ExecuteAsync();

        return writer.ToString();
    }
}

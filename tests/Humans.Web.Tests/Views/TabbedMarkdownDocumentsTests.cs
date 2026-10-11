using System.Text.Encodings.Web;
using Humans.Base.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
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
        var item = new RazorCompiledItemLoader().LoadItems(typeof(TabbedMarkdownDocumentsViewModel).Assembly)
            .Single(i => string.Equals(i.Identifier, "/Views/Shared/_TabbedMarkdownDocuments.cshtml", StringComparison.Ordinal));
        var page = (RazorPage<TabbedMarkdownDocumentsViewModel>)Activator.CreateInstance(item.Type)!;
        item.Type.GetProperty("Html")!.SetValue(page, Substitute.For<IHtmlHelper<TabbedMarkdownDocumentsViewModel>>());
        page.HtmlEncoder = HtmlEncoder.Default;
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<TabbedMarkdownDocumentsViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model,
        };
        page.ViewContext = new ViewContext { Writer = writer, ViewData = data };
        page.ViewData = data;

        await page.ExecuteAsync();

        Assert.Contains(HtmlEncoder.Default.Encode(model.EmptyMessage), writer.ToString(), StringComparison.Ordinal);
    }
}

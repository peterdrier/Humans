using Humans.Consent.Contracts;
using Humans.Consent.Controllers;
using Humans.Consent.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Controllers;

public sealed class LegalControllerTests
{
    [HumansFact]
    public async Task Index_WhenContentIsUnavailable_UsesLocalizedEmptyMessage()
    {
        var documents = Substitute.For<ILegalDocumentService>();
        documents.GetAvailableDocuments().Returns(
            new[] { new LegalDocumentDefinition("statutes", "Statutes", "Estatutos", "ESTATUTOS") });
        documents.GetDocumentContentAsync("statutes").Returns(new Dictionary<string, string>(StringComparer.Ordinal));
        var localizer = Substitute.For<IStringLocalizer<ConsentResource>>();
        localizer["ConsentReview_ContentNotAvailable"].Returns(
            new LocalizedString("ConsentReview_ContentNotAvailable", "Contenido no disponible."));
        var controller = new LegalController(documents, localizer);

        var result = Assert.IsType<ViewResult>(await controller.Index(null));
        var model = Assert.IsType<LegalPageViewModel>(result.Model);

        Assert.Equal("Contenido no disponible.", model.DocumentContent.EmptyMessage);
    }
}

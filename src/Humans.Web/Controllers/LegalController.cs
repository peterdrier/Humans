using Humans.Application.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Route("Legal")]
[AllowAnonymous]
public class LegalController : Controller
{
    private readonly ILegalDocumentService _legalDocService;

    public LegalController(ILegalDocumentService legalDocService)
    {
        _legalDocService = legalDocService;
    }

    [HttpGet("{slug?}")]
    public async Task<IActionResult> Index(string? slug)
    {
        var documents = _legalDocService.GetAvailableDocuments();
        if (documents.Count == 0)
            return NotFound();

        var currentDoc = slug is not null
            ? documents.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase))
            : documents[0];

        if (currentDoc is null)
            return NotFound();

        var content = await _legalDocService.GetDocumentContentAsync(currentDoc.Slug);
        var orderedContent = content.OrderByDisplayLanguage(canonicalFirst: true).ToList();
        var defaultLang = content.GetDefaultDocumentLanguage();

        var viewModel = new LegalPageViewModel
        {
            AllDocuments = documents,
            CurrentSlug = currentDoc.Slug,
            CurrentDocumentName = currentDoc.DisplayName,
            DocumentContent = new TabbedMarkdownDocumentsViewModel
            {
                Documents = orderedContent,
                DefaultLanguage = defaultLang,
                TabsId = "legal-tabs",
                ContentId = "legal-tabs-content",
                ContentStyle = "",
                EmptyMessage = "Document not yet available.",
                UseLegalLanguageLabels = true
            }
        };

        ViewData["Title"] = currentDoc.DisplayName;
        return View(viewModel);
    }
}

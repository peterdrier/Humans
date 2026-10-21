using System.Security.Claims;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Humans.Consent.Controllers;
using Humans.Consent.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Controllers;

public sealed class AdminLegalDocumentsControllerTests
{
    [HumansTheory]
    [InlineData((int)VersionSummaryUpdateResult.NotFound, true)]
    [InlineData((int)VersionSummaryUpdateResult.Updated, true)]
    [InlineData((int)VersionSummaryUpdateResult.TooLong, true)]
    [InlineData((int)VersionSummaryUpdateResult.TooLong, false)]
    public async Task SummaryUpdates_surface_refusals_and_require_an_actor(int outcome, bool signedIn)
    {
        var actor = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var documents = Substitute.For<IAdminLegalDocumentService>();
        documents.UpdateVersionSummaryAsync(documentId, versionId, "Summary", actor, CancellationToken.None)
            .Returns((VersionSummaryUpdateResult)outcome);
        var http = new DefaultHttpContext
        {
            User = signedIn
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"))
                : new ClaimsPrincipal()
        };
        var controller = new AdminLegalDocumentsController(Substitute.For<IUserService>(), documents,
            Substitute.For<ITeamService>(), NullLogger<AdminLegalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };

        var result = await controller.UpdateVersionSummary(documentId, versionId, "Summary");

        if (!signedIn)
        {
            Assert.IsType<UnauthorizedResult>(result);
            Assert.Empty(documents.ReceivedCalls());
        }
        else if ((VersionSummaryUpdateResult)outcome == VersionSummaryUpdateResult.NotFound)
            Assert.IsType<NotFoundResult>(result);
        else
        {
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(AdminLegalDocumentsController.EditLegalDocument), redirect.ActionName);
            Assert.Equal(documentId, redirect.RouteValues!["id"]);
            if ((VersionSummaryUpdateResult)outcome == VersionSummaryUpdateResult.TooLong)
            {
                Assert.Equal("Summary must be at most 2000 characters.", controller.TempData[TempDataKeys.ErrorMessage]);
                Assert.False(controller.TempData.ContainsKey(TempDataKeys.SuccessMessage));
            }
            else
                Assert.Equal("Version summary updated.", controller.TempData[TempDataKeys.SuccessMessage]);
        }
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DocumentReads_PreserveRequestCancellation(int page)
    {
        using var cancellation = new CancellationTokenSource();
        var documents = Substitute.For<IAdminLegalDocumentService>();
        var teams = Substitute.For<ITeamService>();
        var id = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        documents.GetLegalDocumentsAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AdminLegalDocumentListItem>());
        documents.GetLegalDocumentWithVersionsAsync(id, Arg.Any<CancellationToken>())
            .Returns(new AdminLegalDocumentEditDetail(
                id, "Conduct", teamId, true, true, 30, null, default, null, []));
        teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        var controller = new AdminLegalDocumentsController(
            Substitute.For<IUserService>(), documents, teams,
            NullLogger<AdminLegalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token },
            },
        };

        var result = page switch
        {
            0 => await controller.LegalDocuments(teamId),
            1 => await controller.CreateLegalDocument(teamId),
            _ => await controller.EditLegalDocument(id),
        };

        Assert.IsType<ViewResult>(result);
        await teams.Received(1).GetTeamsAsync(cancellation.Token);
        if (page == 0)
            await documents.Received(1).GetLegalDocumentsAsync(teamId, cancellation.Token);
        if (page == 2)
            await documents.Received(1).GetLegalDocumentWithVersionsAsync(id, cancellation.Token);
    }
}

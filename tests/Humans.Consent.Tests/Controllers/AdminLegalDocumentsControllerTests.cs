using Humans.Consent.Controllers;
using Humans.Consent.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Controllers;

public sealed class AdminLegalDocumentsControllerTests
{
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
                id, "Conduct", teamId, true, true, 30, null, default, []));
        teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        var controller = new AdminLegalDocumentsController(
            Substitute.For<IUserService>(), documents, teams, SystemClock.Instance,
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

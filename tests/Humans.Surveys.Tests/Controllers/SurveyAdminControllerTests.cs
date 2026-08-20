using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Surveys.Controllers;
using Humans.Surveys.Domain;
using Humans.Surveys.Models;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Surveys.Tests.Controllers;

public sealed class SurveyAdminControllerTests
{
    [HumansFact]
    public async Task Send_preserves_the_name_of_a_deactivated_audience_team()
    {
        var surveyId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var surveys = Substitute.For<ISurveyService>();
        var teams = Substitute.For<ITeamServiceRead>();
        var editable = new SurveyEditInput(
            Text("Team survey"),
            LocalizedText.Empty,
            LocalizedText.Empty,
            "en",
            false,
            null,
            null,
            SurveyAudienceType.Team,
            teamId,
            null,
            null,
            []);
        surveys.GetForEditAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(surveyId, SurveyStatus.Open, editable));
        surveys.PreviewAudienceCountAsync(surveyId, Arg.Any<CancellationToken>()).Returns(3);
        surveys.GetInviteStatusesAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SurveyInviteStatus>());
        teams.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                teamId,
                "Archived Team",
                null,
                "archived-team",
                IsActive: false,
                IsSystemTeam: false,
                SystemTeamType.None,
                RequiresApproval: false,
                IsPublicPage: false,
                IsHidden: false,
                IsPromotedToDirectory: false,
                Instant.FromUtc(2026, 1, 1, 0, 0),
                []));
        var sut = new SurveyAdminController(
            surveys,
            teams,
            Substitute.For<IUserServiceRead>(),
            NullLogger<SurveyAdminController>.Instance);

        var result = await sut.Send(surveyId, Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<SurveySendViewModel>().Subject;
        model.AudienceTeamName.Should().Be("Archived Team");
        await teams.Received(1).GetTeamAsync(teamId, Arg.Any<CancellationToken>());
        await teams.DidNotReceive().GetTeamsAsync(Arg.Any<CancellationToken>());
    }

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });
}

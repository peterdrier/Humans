using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Controllers;
using Humans.Surveys.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The query parsing, filtering, paging and formatting the machine survey API does on top of
/// <see cref="ISurveyAnalysisRead"/> — ported from the Surveys section's own API tests when the
/// controller moved to <c>/api/backdoor/surveys</c> (nobodies-collective/Humans#1128).
/// Everything below is controller work: the service is a substitute throughout.
/// </summary>
public class BackdoorSurveysControllerTests
{
    private static readonly Instant Submitted = Instant.FromUtc(2026, 6, 4, 9, 30);

    private readonly ISurveyAnalysisRead _surveys = Substitute.For<ISurveyAnalysisRead>();
    private readonly BackdoorSurveysController _sut;

    public BackdoorSurveysControllerTests() =>
        _sut = new BackdoorSurveysController(_surveys, Substitute.For<IUserServiceRead>());

    private static SurveyExportQuestion Choice(Guid id, string prompt, params (string Value, string Label)[] opts) =>
        new(id, prompt, SurveyQuestionType.MultiChoice, [.. opts.Select(o => new SurveyExportOption(o.Value, o.Label))]);

    private static SurveyExportRow Row(
        ResponseAnonymity anon, Instant? at, Guid? userId, string? userName, params SurveyExportAnswer[] answers) =>
        new(Guid.NewGuid(), anon, SurveyInputMethod.UserSpecificLink, "en", at, userId, userName, answers);

    private static List<object> Items(IActionResult result)
    {
        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        return [.. (IEnumerable<object>)value.GetType().GetProperty("items")!.GetValue(value)!];
    }

    // ── Definition ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Definition_returns_NotFound_for_missing_survey()
    {
        var id = Guid.NewGuid();
        _surveys.GetDefinitionAsync(id, Arg.Any<CancellationToken>()).Returns((SurveyDefinitionSnapshot?)null);

        var result = await _sut.Definition(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Definition_serialises_enums_as_strings_and_projects_the_question_graph()
    {
        var id = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        _surveys.GetDefinitionAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyDefinitionSnapshot(
            id, "My Survey", SurveyStatus.Open, "en",
            [
                new SurveyDefinitionQuestion(
                    questionId, 1, 0, SurveyQuestionType.Grid, "Availability", null, true, null, null,
                    ShowIf: null,
                    GridSelectionMode: GridSelectionMode.Single,
                    GridRows: [new SurveyExportGridRow("monday", "Monday")],
                    Images: [],
                    Options: [new SurveyExportOption("morning", "Morning")]),
            ]));

        var result = await _sut.Definition(id, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""title"":""My Survey""");
        json.Should().Contain(@"""status"":""Open""");
        json.Should().Contain(@"""type"":""Grid""");
        json.Should().Contain(@"""gridSelectionMode"":""Single""");
        json.Should().Contain(@"""value"":""monday""");
        json.Should().Contain(@"""label"":""Morning""");
    }

    [HumansFact]
    public async Task Definition_carries_information_markdown_and_images()
    {
        var id = Guid.NewGuid();
        _surveys.GetDefinitionAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyDefinitionSnapshot(
            id, "My Survey", SurveyStatus.Open, "en",
            [
                new SurveyDefinitionQuestion(
                    Guid.NewGuid(), 1, 0, SurveyQuestionType.Information, "Forecast", "**Read this first**",
                    false, null, null, null, null, GridRows: [],
                    Images: [new SurveyDefinitionImage(Guid.NewGuid(), "/uploads/surveys/fire.png", "Fire risk", "Fire risk table")],
                    Options: []),
            ]));

        var result = await _sut.Definition(id, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""type"":""Information""");
        json.Should().Contain(@"""markdown"":""**Read this first**""");
        json.Should().Contain(@"""url"":""/uploads/surveys/fire.png""");
        json.Should().Contain(@"""altText"":""Fire risk table""");
    }

    // ── Responses ───────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Responses_returns_NotFound_for_missing_survey()
    {
        var id = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns((SurveyResponseExport?)null);

        var result = await _sut.Responses(id, null, null, 100, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Responses_filters_by_anonymity()
    {
        var id = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en", [],
            [
                Row(ResponseAnonymity.Identified, Submitted, Guid.NewGuid(), "Sparkle"),
                Row(ResponseAnonymity.Anonymous, Submitted, null, null),
            ]));

        var result = await _sut.Responses(
            id, ResponseAnonymity.Anonymous, null, 100, null, null, Xunit.TestContext.Current.CancellationToken);

        var items = Items(result);
        items.Should().HaveCount(1);
        items[0].GetType().GetProperty("anonymity")!.GetValue(items[0]).Should().Be("Anonymous");
    }

    [HumansFact]
    public async Task Responses_carries_the_identity_columns_only_the_export_populated()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en", [],
            [
                Row(ResponseAnonymity.Identified, Submitted, userId, "Sparkle"),
                Row(ResponseAnonymity.Anonymous, Submitted, null, null),
            ]));

        var items = Items(await _sut.Responses(id, null, null, 100, null, null, Xunit.TestContext.Current.CancellationToken));

        items[0].GetType().GetProperty("userName")!.GetValue(items[0]).Should().Be("Sparkle");
        items[1].GetType().GetProperty("userId")!.GetValue(items[1]).Should().BeNull();
        items[1].GetType().GetProperty("userName")!.GetValue(items[1]).Should().BeNull();
    }

    [HumansFact]
    public async Task Responses_filters_by_since_instant()
    {
        var id = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en", [],
            [
                Row(ResponseAnonymity.Anonymous, Instant.FromUtc(2026, 1, 1, 0, 0), null, null),
                Row(ResponseAnonymity.Anonymous, Submitted, null, null),
            ]));

        var result = await _sut.Responses(
            id, null, "2026-03-01T00:00:00Z", 100, null, null, Xunit.TestContext.Current.CancellationToken);

        Items(result).Should().HaveCount(1);   // only the June row survives the March cutoff
    }

    [HumansFact]
    public async Task Responses_rejects_malformed_since()
    {
        var id = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>())
            .Returns(new SurveyResponseExport(id, "T", "en", [], []));

        var result = await _sut.Responses(id, null, "not-a-date", 100, null, null, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [HumansFact]
    public async Task Responses_pages_with_cursor()
    {
        var id = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en", [],
            [.. Enumerable.Range(0, 3).Select(_ => Row(ResponseAnonymity.Anonymous, Submitted, null, null))]));

        var first = await _sut.Responses(
            id, null, null, limit: 2, cursor: null, format: null, Xunit.TestContext.Current.CancellationToken);
        Items(first).Should().HaveCount(2);
        var firstValue = ((OkObjectResult)first).Value!;
        var nextCursor = (string?)firstValue.GetType().GetProperty("nextCursor")!.GetValue(firstValue);
        nextCursor.Should().NotBeNull();

        var second = await _sut.Responses(
            id, null, null, limit: 2, cursor: nextCursor, format: null, Xunit.TestContext.Current.CancellationToken);
        Items(second).Should().HaveCount(1);
        var secondValue = ((OkObjectResult)second).Value!;
        ((string?)secondValue.GetType().GetProperty("nextCursor")!.GetValue(secondValue)).Should().BeNull();
    }

    [HumansFact]
    public async Task Responses_format_md_returns_markdown_content()
    {
        var id = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en",
            [Choice(questionId, "Pick", ("a", "Apple"), ("b", "Banana"))],
            [Row(ResponseAnonymity.Anonymous, Submitted, null, null,
                new SurveyExportAnswer(questionId, ["a", "b"], ["Apple", "Banana"], null, null))]));

        var result = await _sut.Responses(id, null, null, 100, null, "md", Xunit.TestContext.Current.CancellationToken);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().StartWith("text/markdown");
        content.Content.Should().Contain("| Pick |");
        content.Content.Should().Contain("a\\|b");   // pipe escaped for the MD table
    }

    [HumansFact]
    public async Task Responses_projects_resolved_grid_selections()
    {
        var id = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResponseExport(
            id, "T", "en",
            [
                new SurveyExportQuestion(
                    questionId, "Availability", SurveyQuestionType.Grid,
                    [new SurveyExportOption("morning", "Morning")],
                    GridSelectionMode.Single,
                    [new SurveyExportGridRow("monday", "Monday")]),
            ],
            [
                Row(ResponseAnonymity.Anonymous, Submitted, null, null,
                    new SurveyExportAnswer(
                        questionId, [], [], null, null,
                        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["monday"] = ["morning"] },
                        [new ResolvedGridSelection("monday", "Monday", ["morning"], ["Morning"])])),
            ]));

        var result = await _sut.Responses(id, null, null, 100, null, null, Xunit.TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        json.Should().Contain(@"""gridSelections"":{""monday"":[""morning""]}");
        json.Should().Contain(@"""rowValue"":""monday""");
        json.Should().Contain(@"""columnLabels"":[""Morning""]");
    }

    // ── Aggregates ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Aggregates_returns_NotFound_for_missing_survey()
    {
        var id = Guid.NewGuid();
        _surveys.GetResultsAsync(id, Arg.Any<CancellationToken>()).Returns((SurveyResultsView?)null);

        var result = await _sut.Aggregates(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Aggregates_projects_funnel_and_status_as_string()
    {
        var id = Guid.NewGuid();
        _surveys.GetResultsAsync(id, Arg.Any<CancellationToken>()).Returns(new SurveyResultsView(
            id, "T", SurveyStatus.Closed, 10, 4, 0.4,
            new SurveyFunnel(LinkStarted: 6, LinkFinished: 3, SlugStarted: 2, SlugFinished: 1),
            [], []));

        var result = await _sut.Aggregates(id, Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("status")!.GetValue(value).Should().Be("Closed");
        value.GetType().GetProperty("responseRate")!.GetValue(value).Should().Be(0.4);
        var funnel = value.GetType().GetProperty("funnel")!.GetValue(value)!;
        funnel.GetType().GetProperty("linkStarted")!.GetValue(funnel).Should().Be(6);
        funnel.GetType().GetProperty("slugFinished")!.GetValue(funnel).Should().Be(1);
    }
}

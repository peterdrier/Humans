using AwesomeAssertions;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Web.Controllers;
using Humans.Web.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="SurveysApiController"/> and its <see cref="SurveyApiKeyAuthFilter"/>.
/// Mocks <see cref="ISurveyService"/> and exercises the controller directly (no HTTP roundtrip).
/// The filter behaviour mirrors the Issues/Feedback API key tests (503 unset, 401 wrong, pass-through).
/// </summary>
public class SurveysApiControllerTests
{
    private static readonly Instant Submitted = Instant.FromUtc(2026, 6, 4, 9, 30);

    private readonly ISurveyService _surveys = Substitute.For<ISurveyService>();
    private readonly SurveysApiController _sut;

    public SurveysApiControllerTests()
    {
        _sut = new SurveysApiController(_surveys, Substitute.For<IUserServiceRead>());
    }

    private static LocalizedText Text(string en) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static SurveyExportQuestion Choice(Guid id, string prompt, params (string Value, string Label)[] opts) =>
        new(id, prompt, SurveyQuestionType.MultiChoice, opts.Select(o => new SurveyExportOption(o.Value, o.Label)).ToList());

    private static SurveyExportRow Row(ResponseAnonymity anon, Instant? at, Guid? userId, string? userName, params SurveyExportAnswer[] answers) =>
        new(Guid.NewGuid(), anon, SurveyInputMethod.UserSpecificLink, "en", at, userId, userName, answers);

    // ── Definition ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Definition_returns_NotFound_for_missing_survey()
    {
        var id = Guid.NewGuid();
        _surveys.GetForEditAsync(id, Arg.Any<CancellationToken>()).Returns((SurveyDetail?)null);

        var result = await _sut.Definition(id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Definition_resolves_prompt_and_serialises_type_as_string()
    {
        var id = Guid.NewGuid();
        var qId = Guid.NewGuid();
        var editable = new SurveyEditInput(
            Text("My Survey"), LocalizedText.Empty, LocalizedText.Empty, "en", false, null, null, null, null, null,
            [
                new QuestionInput(qId, 1, 0, SurveyQuestionType.SingleChoice, Text("Pick one"), LocalizedText.Empty,
                    true, null, null, LocalizedText.Empty, LocalizedText.Empty, null,
                    [new OptionInput(Guid.NewGuid(), 0, "a", Text("Apple"))]),
            ]);
        _surveys.GetForEditAsync(id, Arg.Any<CancellationToken>())
            .Returns(new SurveyDetail(id, SurveyStatus.Open, editable));

        var result = await _sut.Definition(id, Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("title")!.GetValue(value).Should().Be("My Survey");
        value.GetType().GetProperty("status")!.GetValue(value).Should().Be("Open");
        var questions = ((IEnumerable<object>)value.GetType().GetProperty("questions")!.GetValue(value)!).ToList();
        questions.Should().HaveCount(1);
        questions[0].GetType().GetProperty("type")!.GetValue(questions[0]).Should().Be("SingleChoice");
        questions[0].GetType().GetProperty("prompt")!.GetValue(questions[0]).Should().Be("Pick one");
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
        var export = new SurveyResponseExport(id, "T", "en", [],
            [
                Row(ResponseAnonymity.Identified, Submitted, Guid.NewGuid(), "Sparkle"),
                Row(ResponseAnonymity.Anonymous, Submitted, null, null),
            ]);
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(export);

        var result = await _sut.Responses(id, ResponseAnonymity.Anonymous, null, 100, null, null, Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        var items = ((IEnumerable<object>)value.GetType().GetProperty("items")!.GetValue(value)!).ToList();
        items.Should().HaveCount(1);
        items[0].GetType().GetProperty("anonymity")!.GetValue(items[0]).Should().Be("Anonymous");
    }

    [HumansFact]
    public async Task Responses_filters_by_since_instant()
    {
        var id = Guid.NewGuid();
        var early = Instant.FromUtc(2026, 1, 1, 0, 0);
        var export = new SurveyResponseExport(id, "T", "en", [],
            [
                Row(ResponseAnonymity.Anonymous, early, null, null),
                Row(ResponseAnonymity.Anonymous, Submitted, null, null),
            ]);
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(export);

        var result = await _sut.Responses(id, null, "2026-03-01T00:00:00Z", 100, null, null, Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        var items = ((IEnumerable<object>)value.GetType().GetProperty("items")!.GetValue(value)!).ToList();
        items.Should().HaveCount(1);   // only the June row survives the March cutoff
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
        var rows = Enumerable.Range(0, 3)
            .Select(_ => Row(ResponseAnonymity.Anonymous, Submitted, null, null))
            .ToList();
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>())
            .Returns(new SurveyResponseExport(id, "T", "en", [], rows));

        var first = await _sut.Responses(id, null, null, limit: 2, cursor: null, format: null, Xunit.TestContext.Current.CancellationToken);
        var firstValue = first.Should().BeOfType<OkObjectResult>().Subject.Value!;
        var firstItems = ((IEnumerable<object>)firstValue.GetType().GetProperty("items")!.GetValue(firstValue)!).ToList();
        firstItems.Should().HaveCount(2);
        var nextCursor = (string?)firstValue.GetType().GetProperty("nextCursor")!.GetValue(firstValue);
        nextCursor.Should().NotBeNull();

        var second = await _sut.Responses(id, null, null, limit: 2, cursor: nextCursor, format: null, Xunit.TestContext.Current.CancellationToken);
        var secondValue = second.Should().BeOfType<OkObjectResult>().Subject.Value!;
        var secondItems = ((IEnumerable<object>)secondValue.GetType().GetProperty("items")!.GetValue(secondValue)!).ToList();
        secondItems.Should().HaveCount(1);
        ((string?)secondValue.GetType().GetProperty("nextCursor")!.GetValue(secondValue)).Should().BeNull();
    }

    [HumansFact]
    public async Task Responses_format_md_returns_markdown_content()
    {
        var id = Guid.NewGuid();
        var qId = Guid.NewGuid();
        var export = new SurveyResponseExport(id, "T", "en",
            [Choice(qId, "Pick", ("a", "Apple"), ("b", "Banana"))],
            [Row(ResponseAnonymity.Anonymous, Submitted, null, null,
                new SurveyExportAnswer(qId, ["a", "b"], ["Apple", "Banana"], null, null))]);
        _surveys.GetResponseExportAsync(id, Arg.Any<CancellationToken>()).Returns(export);

        var result = await _sut.Responses(id, null, null, 100, null, "md", Xunit.TestContext.Current.CancellationToken);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().StartWith("text/markdown");
        content.Content.Should().Contain("| Pick |");
        content.Content.Should().Contain("a\\|b");   // pipe escaped for the MD table
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
        var results = new SurveyResultsView(id, "T", SurveyStatus.Closed, 10, 4, 0.4,
            new SurveyFunnel(LinkStarted: 6, LinkFinished: 3, SlugStarted: 2, SlugFinished: 1),
            [], []);
        _surveys.GetResultsAsync(id, Arg.Any<CancellationToken>()).Returns(results);

        var result = await _sut.Aggregates(id, Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        value.GetType().GetProperty("status")!.GetValue(value).Should().Be("Closed");
        value.GetType().GetProperty("responseRate")!.GetValue(value).Should().Be(0.4);
        var funnel = value.GetType().GetProperty("funnel")!.GetValue(value)!;
        funnel.GetType().GetProperty("linkStarted")!.GetValue(funnel).Should().Be(6);
        funnel.GetType().GetProperty("slugFinished")!.GetValue(funnel).Should().Be(1);
    }

    // ── ApiKey filter ─────────────────────────────────────────────────────────

    [HumansFact]
    public void ApiKey_missing_returns_503_when_settings_have_empty_key()
    {
        var filter = new SurveyApiKeyAuthFilter(Options.Create(new SurveyApiSettings { ApiKey = string.Empty }));
        var ctx = MakeAuthFilterContext(headerKey: null);

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(503);
    }

    [HumansFact]
    public void ApiKey_wrong_returns_401_when_settings_configured()
    {
        var filter = new SurveyApiKeyAuthFilter(Options.Create(new SurveyApiSettings { ApiKey = "right-key" }));
        var ctx = MakeAuthFilterContext(headerKey: "wrong-key");

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public void ApiKey_matching_passes_through()
    {
        var filter = new SurveyApiKeyAuthFilter(Options.Create(new SurveyApiSettings { ApiKey = "right-key" }));
        var ctx = MakeAuthFilterContext(headerKey: "right-key");

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeNull();
    }

    private static AuthorizationFilterContext MakeAuthFilterContext(string? headerKey)
    {
        var http = new DefaultHttpContext();
        if (headerKey is not null)
        {
            http.Request.Headers["X-Api-Key"] = headerKey;
        }
        var actionContext = new ActionContext(
            http,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, Array.Empty<IFilterMetadata>());
    }
}

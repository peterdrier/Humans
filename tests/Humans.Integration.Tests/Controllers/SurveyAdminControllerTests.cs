using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Real form-POST coverage for the survey builder's datetime-local fields
/// (nobodies-collective/Humans#932): browsers post OpensAt/ClosesAt WITHOUT
/// seconds, which the default NodaTime TypeConverter rejects. These tests
/// exercise the full MVC model-binding path via LocalDateTimeModelBinder.
/// </summary>
public class SurveyAdminControllerTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    [HumansFact(Timeout = 60000)]
    public async Task Save_binds_OpensAt_and_ClosesAt_from_datetime_local_wire_formats()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        // GET the builder to seed the antiforgery token + cookie.
        var createResp = await Client.GetAsync("/Survey/Admin/Create", Xunit.TestContext.Current.CancellationToken);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = ExtractAntiForgeryToken(await createResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken));
        token.Should().NotBeNullOrEmpty();

        // OpensAt uses the browser's seconds-less wire format; ClosesAt includes
        // seconds (some pickers do). Both must bind.
        var saveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", token!),
            ("Title[en]", $"Integration test survey {Guid.NewGuid():N}"),
            ("OpensAt", "2026-07-14T10:30"),
            ("ClosesAt", "2026-08-01T18:45:30")), Xunit.TestContext.Current.CancellationToken);

        // A model-binding failure re-renders the builder as 200; success redirects to Edit/{id}.
        ((int)saveResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);
        var location = saveResp.Headers.Location!.ToString();
        var idMatch = Regex.Match(location, "/Survey/Admin/Edit/(?<id>[0-9a-fA-F-]{36})",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        idMatch.Success.Should().BeTrue($"expected redirect to Edit/{{id}}, got '{location}'");
        var surveyId = Guid.Parse(idMatch.Groups["id"].Value);

        // Persisted instants must round-trip the posted local times in Europe/Madrid.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var survey = await db.Surveys.AsNoTracking()
            .FirstAsync(s => s.Id == surveyId, Xunit.TestContext.Current.CancellationToken);

        survey.OpensAt.Should().Be(
            new LocalDateTime(2026, 7, 14, 10, 30).InZoneLeniently(Madrid).ToInstant());
        survey.ClosesAt.Should().Be(
            new LocalDateTime(2026, 8, 1, 18, 45, 30).InZoneLeniently(Madrid).ToInstant());
    }

    [HumansFact(Timeout = 60000)]
    public async Task Save_binds_and_persists_grid_rows_columns_and_selection_mode()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var ct = Xunit.TestContext.Current.CancellationToken;
        var createResp = await Client.GetAsync("/Survey/Admin/Create", ct);
        var token = ExtractAntiForgeryToken(await createResp.Content.ReadAsStringAsync(ct));
        var questionId = Guid.NewGuid();

        var saveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", token!),
            ("Title[en]", $"Grid integration survey {Guid.NewGuid():N}"),
            ("Questions.Index", "grid"),
            ("Questions[grid].Id", questionId.ToString()),
            ("Questions[grid].PageNumber", "1"),
            ("Questions[grid].Type", nameof(SurveyQuestionType.Grid)),
            ("Questions[grid].Prompt[en]", "When can you attend?"),
            ("Questions[grid].IsRequired", "true"),
            ("Questions[grid].GridSelectionMode", nameof(GridSelectionMode.Multiple)),
            ("Questions[grid].Options.Index", "morning"),
            ("Questions[grid].Options[morning].Value", "morning"),
            ("Questions[grid].Options[morning].Label[en]", "Morning"),
            ("Questions[grid].Options.Index", "afternoon"),
            ("Questions[grid].Options[afternoon].Value", "afternoon"),
            ("Questions[grid].Options[afternoon].Label[en]", "Afternoon"),
            ("Questions[grid].GridRows.Index", "monday"),
            ("Questions[grid].GridRows[monday].Value", "monday"),
            ("Questions[grid].GridRows[monday].Label[en]", "Monday"),
            ("Questions[grid].GridRows.Index", "tuesday"),
            ("Questions[grid].GridRows[tuesday].Value", "tuesday"),
            ("Questions[grid].GridRows[tuesday].Label[en]", "Tuesday")), ct);

        var saveHtml = await saveResp.Content.ReadAsStringAsync(ct);
        var validationMessages = Regex.Matches(
                saveHtml,
                "<li>(?<message>.*?)</li>",
                RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
                TimeSpan.FromSeconds(2))
            .Select(match => match.Groups["message"].Value)
            .ToList();
        ((int)saveResp.StatusCode).Should().BeOneOf(
            [(int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect],
            $"a valid Grid post should redirect; validation messages were: {string.Join(" | ", validationMessages)}");
        var idMatch = Regex.Match(
            saveResp.Headers.Location!.ToString(),
            "/Survey/Admin/Edit/(?<id>[0-9a-fA-F-]{36})",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        idMatch.Success.Should().BeTrue();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var surveyId = Guid.Parse(idMatch.Groups["id"].Value);
        var question = (await db.Surveys.AsNoTracking()
                .Include(survey => survey.Questions)
                .ThenInclude(q => q.Options)
                .SingleAsync(survey => survey.Id == surveyId, ct))
            .Questions.Should().ContainSingle().Subject;

        question.Id.Should().Be(questionId);
        question.Type.Should().Be(SurveyQuestionType.Grid);
        question.IsRequired.Should().BeTrue();
        question.GridSelectionMode.Should().Be(GridSelectionMode.Multiple);
        question.GridRows!.Select(row => row.Value).Should().ContainInOrder("monday", "tuesday");
        question.Options.OrderBy(option => option.Order).Select(option => option.Value)
            .Should().ContainInOrder("morning", "afternoon");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Save_rejects_grid_when_selection_mode_is_omitted()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var ct = Xunit.TestContext.Current.CancellationToken;
        var createResp = await Client.GetAsync("/Survey/Admin/Create", ct);
        var token = ExtractAntiForgeryToken(await createResp.Content.ReadAsStringAsync(ct));

        var saveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", token!),
            ("Title[en]", $"Grid missing mode {Guid.NewGuid():N}"),
            ("Questions.Index", "grid"),
            ("Questions[grid].Id", Guid.NewGuid().ToString()),
            ("Questions[grid].PageNumber", "1"),
            ("Questions[grid].Type", nameof(SurveyQuestionType.Grid)),
            ("Questions[grid].Prompt[en]", "When can you attend?"),
            ("Questions[grid].Options.Index", "column"),
            ("Questions[grid].Options[column].Value", "column-1"),
            ("Questions[grid].Options[column].Label[en]", "Available"),
            ("Questions[grid].GridRows.Index", "row"),
            ("Questions[grid].GridRows[row].Value", "row-1"),
            ("Questions[grid].GridRows[row].Label[en]", "Monday")), ct);

        saveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await saveResp.Content.ReadAsStringAsync(ct))
            .Should().Contain("must choose a selection mode");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Save_adds_grid_with_client_generated_ids_to_existing_survey()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var createResp = await Client.GetAsync("/Survey/Admin/Create", ct);
        var createToken = ExtractAntiForgeryToken(await createResp.Content.ReadAsStringAsync(ct));
        var initialSaveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", createToken!),
            ("Title[en]", $"Grid update integration survey {Guid.NewGuid():N}")), ct);
        var surveyId = ExtractSurveyId(initialSaveResp);

        var editResp = await Client.GetAsync($"/Survey/Admin/Edit/{surveyId}", ct);
        var editToken = ExtractAntiForgeryToken(await editResp.Content.ReadAsStringAsync(ct));
        var questionId = Guid.NewGuid();
        var columnId = Guid.NewGuid();

        var updateResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", editToken!),
            ("Id", surveyId.ToString()),
            ("Title[en]", $"Updated grid integration survey {Guid.NewGuid():N}"),
            ("Questions.Index", "grid"),
            ("Questions[grid].Id", questionId.ToString()),
            ("Questions[grid].PageNumber", "1"),
            ("Questions[grid].Type", nameof(SurveyQuestionType.Grid)),
            ("Questions[grid].Prompt[en]", "When can you attend?"),
            ("Questions[grid].GridSelectionMode", nameof(GridSelectionMode.Single)),
            ("Questions[grid].Options.Index", "column"),
            ("Questions[grid].Options[column].Id", columnId.ToString()),
            ("Questions[grid].Options[column].Value", "column-1"),
            ("Questions[grid].Options[column].Label[en]", "Available"),
            ("Questions[grid].GridRows.Index", "row"),
            ("Questions[grid].GridRows[row].Value", "row-1"),
            ("Questions[grid].GridRows[row].Label[en]", "Monday")), ct);

        ((int)updateResp.StatusCode).Should().BeOneOf(
            [(int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect]);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var question = (await db.Surveys.AsNoTracking()
                .Include(survey => survey.Questions)
                .ThenInclude(q => q.Options)
                .SingleAsync(survey => survey.Id == surveyId, ct))
            .Questions.Should().ContainSingle().Subject;

        question.Id.Should().Be(questionId);
        question.Options.Should().ContainSingle(option => option.Id == columnId);
        question.GridRows.Should().ContainSingle(row => row.Value == "row-1");

        var secondEditResp = await Client.GetAsync($"/Survey/Admin/Edit/{surveyId}", ct);
        var secondEditToken = ExtractAntiForgeryToken(await secondEditResp.Content.ReadAsStringAsync(ct));
        var secondColumnId = Guid.NewGuid();
        var addColumnResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", secondEditToken!),
            ("Id", surveyId.ToString()),
            ("Title[en]", "Updated grid integration survey"),
            ("Questions.Index", "grid"),
            ("Questions[grid].Id", questionId.ToString()),
            ("Questions[grid].PageNumber", "1"),
            ("Questions[grid].Type", nameof(SurveyQuestionType.Grid)),
            ("Questions[grid].Prompt[en]", "When can you attend?"),
            ("Questions[grid].GridSelectionMode", nameof(GridSelectionMode.Single)),
            ("Questions[grid].Options.Index", "first-column"),
            ("Questions[grid].Options[first-column].Id", columnId.ToString()),
            ("Questions[grid].Options[first-column].Value", "column-1"),
            ("Questions[grid].Options[first-column].Label[en]", "Available"),
            ("Questions[grid].Options.Index", "second-column"),
            ("Questions[grid].Options[second-column].Id", secondColumnId.ToString()),
            ("Questions[grid].Options[second-column].Value", "column-2"),
            ("Questions[grid].Options[second-column].Label[en]", "Preferred"),
            ("Questions[grid].GridRows.Index", "row"),
            ("Questions[grid].GridRows[row].Value", "row-1"),
            ("Questions[grid].GridRows[row].Label[en]", "Monday")), ct);

        ((int)addColumnResp.StatusCode).Should().BeOneOf(
            [(int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect]);

        db.ChangeTracker.Clear();
        var updatedQuestion = (await db.Surveys.AsNoTracking()
                .Include(survey => survey.Questions)
                .ThenInclude(q => q.Options)
                .SingleAsync(survey => survey.Id == surveyId, ct))
            .Questions.Should().ContainSingle().Subject;
        updatedQuestion.Options.Select(option => option.Id)
            .Should().BeEquivalentTo([columnId, secondColumnId]);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }

    private static FormUrlEncodedContent BuildForm(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(f =>
            new KeyValuePair<string, string>(f.Key, f.Value)));
    }

    private static Guid ExtractSurveyId(HttpResponseMessage response)
    {
        ((int)response.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);
        var match = Regex.Match(
            response.Headers.Location!.ToString(),
            "/Survey/Admin/Edit/(?<id>[0-9a-fA-F-]{36})",
            RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        match.Success.Should().BeTrue();
        return Guid.Parse(match.Groups["id"].Value);
    }
}

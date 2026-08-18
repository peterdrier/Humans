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
    public async Task Save_rejects_team_audience_without_a_team()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var token = await GetCreateTokenAsync();
        var title = $"Invalid team audience {Guid.NewGuid():N}";

        var saveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", token),
            ("Title[en]", title),
            ("AudienceType", nameof(SurveyAudienceType.Team))),
            Xunit.TestContext.Current.CancellationToken);

        saveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await saveResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        html.Should().Contain("Choose a team for the Team audience.");

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var surveys = await db.Surveys.AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        surveys.Any(s => s.Title.Values.Values.Contains(title, StringComparer.Ordinal))
            .Should().BeFalse();
    }

    [HumansFact(Timeout = 60000)]
    public async Task Save_and_review_recipients_redirects_to_send_with_persisted_audience()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var token = await GetCreateTokenAsync();
        var title = $"Review recipients {Guid.NewGuid():N}";

        var saveResp = await Client.PostAsync("/Survey/Admin/Save", BuildForm(
            ("__RequestVerificationToken", token),
            ("Title[en]", title),
            ("AudienceType", nameof(SurveyAudienceType.AllActiveMembers)),
            ("submitAction", "save-review")),
            Xunit.TestContext.Current.CancellationToken);

        ((int)saveResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);
        var location = saveResp.Headers.Location!.ToString();
        var idMatch = Regex.Match(location, "/Survey/Admin/Send/(?<id>[0-9a-fA-F-]{36})",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        idMatch.Success.Should().BeTrue($"expected redirect to Send/{{id}}, got '{location}'");
        var surveyId = Guid.Parse(idMatch.Groups["id"].Value);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var survey = await db.Surveys.AsNoTracking()
            .FirstAsync(s => s.Id == surveyId, Xunit.TestContext.Current.CancellationToken);
        survey.AudienceType.Should().Be(SurveyAudienceType.AllActiveMembers);

        var sendResp = await Client.GetAsync(location, Xunit.TestContext.Current.CancellationToken);
        sendResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sendHtml = await sendResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        var sendToken = ExtractAntiForgeryToken(sendHtml);
        sendToken.Should().NotBeNullOrEmpty();

        var openResp = await Client.PostAsync($"/Survey/Admin/Open/{surveyId}", BuildForm(
            ("__RequestVerificationToken", sendToken!),
            ("continueToSend", "true")), Xunit.TestContext.Current.CancellationToken);

        ((int)openResp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Found, (int)HttpStatusCode.Redirect);
        openResp.Headers.Location!.ToString().Should().Be($"/Survey/Admin/Send/{surveyId}");
    }

    private async Task<string> GetCreateTokenAsync()
    {
        var createResp = await Client.GetAsync(
            "/Survey/Admin/Create", Xunit.TestContext.Current.CancellationToken);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = ExtractAntiForgeryToken(
            await createResp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken));
        token.Should().NotBeNullOrEmpty();
        return token!;
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
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The controller unit tests skip model binding; this runs the wire model through MVC so a missing field is Tasks'
/// 422 problem list, not the automatic 400, and so is a priority number no priority has.
/// </summary>
public class BackdoorTasksBulkTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60000)]
    public async Task Bulk_with_missing_title_empty_key_and_unknown_priority_is_422_listing_all()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var adminId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var key = await IssueKeyAsync(adminId, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/backdoor/tasks/bulk")
        {
            Content = JsonContent.Create(new
            {
                tasks = new object[]
                {
                    new { key = "no-title", team = "volunteers" },
                    new { key = "", team = "volunteers", title = "No key" },
                    new { key = "loud", team = "volunteers", title = "Too loud", priority = 99 },
                },
            }),
        };
        request.Headers.Add("X-Api-Key", key);

        // A separate client: the admin's session cookie must not stand in for the key.
        using var keyClient = Factory.CreateClient();
        var response = await keyClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        response.StatusCode.Should().Be((HttpStatusCode)422, body);
        var problems = JsonDocument.Parse(body).RootElement.GetProperty("problems").EnumerateArray()
            .Select(p => (Index: p.GetProperty("index").GetInt32(), Field: p.GetProperty("field").GetString()))
            .ToList();
        problems.Should().Contain((0, "title"), body);
        problems.Should().Contain((1, "key"), body);
        problems.Should().Contain((2, "priority"), body);
    }

    private async Task<string> IssueKeyAsync(Guid ownerUserId, CancellationToken ct)
    {
        var page = await Client.GetStringAsync("/Backdoor", ct);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<t>[^\"]+)\"",
            RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2)).Groups["t"].Value;

        var issued = await Client.PostAsync("/Backdoor/Issue", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", token),
            new("ownerUserId", ownerUserId.ToString()),
            new("label", "integration"),
        ]), ct);
        issued.StatusCode.Should().Be(HttpStatusCode.Found);

        var shown = await Client.GetStringAsync("/Backdoor", ct);
        var match = Regex.Match(shown, "user-select-all[^>]*>(?<k>[^<]+)<", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));
        match.Success.Should().BeTrue("the new key is shown once after the redirect");
        return match.Groups["k"].Value.Trim();
    }
}

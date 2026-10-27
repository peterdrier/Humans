using Humans.Email.Contracts;
using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// <c>GuestAccountController.UpdatePreference</c> records <c>UpdateSource</c> as <c>"MagicLink"</c> for an anonymous
/// token POST and <c>"Guest"</c> for a session POST.
/// </summary>
/// <remarks>
/// The POST must change the row: <see cref="MessageCategory.VolunteerUpdates"/> (default OptedOut=false) with
/// <c>emailEnabled=false</c>. A no-op POST short-circuits as idempotent, leaving <c>"Default"</c> behind a 200, and the
/// test passes for nothing.
/// </remarks>
public class UnsubscribeFlowTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 30_000)]
    public async Task TokenDriven_UpdatePreference_AttributesUpdateSourceToMagicLink()
    {
        Guid userId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            userId = await SeedBareUserAsync(scope, $"untoken-{Guid.NewGuid():N}@x.test");
        }

        // Must flip OptedOut, or the service short-circuits and the row stays "Default".
        string token;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var commPrefService = scope.ServiceProvider.GetRequiredService<Humans.Email.Services.ICommunicationPreferenceManagement>();
            var url = commPrefService.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.VolunteerUpdates);
            token = new Uri(url).Segments[^1];
        }

        var (formToken, cookie) = await GetAntiforgeryAsync(
            $"/Email/Guest/CommunicationPreferences?utoken={Uri.EscapeDataString(token)}");

        var resp = await PostFormWithAntiforgeryAsync(
            "/Email/Guest/CommunicationPreferences/Update",
            formToken,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["category"] = MessageCategory.VolunteerUpdates.ToString(),
                ["emailEnabled"] = "false",
                ["alertEnabled"] = "true",
                ["utoken"] = token,
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the AJAX update endpoint returns 200 OK on success (got {(int)resp.StatusCode}).");

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<Humans.Email.Data.EmailDbContext>();
        var pref = await db.CommunicationPreferences
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId && p.Category == MessageCategory.VolunteerUpdates, Xunit.TestContext.Current.CancellationToken);
        pref.OptedOut.Should().BeTrue("emailEnabled=false maps to OptedOut=true.");
        pref.UpdateSource.Should().Be("MagicLink",
            "anonymous token-driven updates must be attributed to MagicLink, " +
            "distinct from the seeded \"Default\" or session-driven \"Guest\".");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task SessionDriven_UpdatePreference_AttributesUpdateSourceToGuest()
    {
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // No utoken, so the authenticated session wins: fromToken=false, source "Guest".
        var (formToken, cookie) = await GetAntiforgeryAsync("/Email/Guest/CommunicationPreferences");

        var resp = await PostFormWithAntiforgeryAsync(
            "/Email/Guest/CommunicationPreferences/Update",
            formToken,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["category"] = MessageCategory.VolunteerUpdates.ToString(),
                ["emailEnabled"] = "false",
                ["alertEnabled"] = "true",
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"the AJAX update endpoint returns 200 OK on success (got {(int)resp.StatusCode}).");

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<Humans.Email.Data.EmailDbContext>();
        var pref = await db.CommunicationPreferences
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId && p.Category == MessageCategory.VolunteerUpdates, Xunit.TestContext.Current.CancellationToken);
        pref.OptedOut.Should().BeTrue("emailEnabled=false maps to OptedOut=true.");
        pref.UpdateSource.Should().Be("Guest",
            "session-driven updates must be attributed to Guest, distinct from MagicLink.");
    }

    private static async Task<Guid> SeedBareUserAsync(IServiceScope scope, string email)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Seeded Human",
            Email = email,
            UserName = email,
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        };
        var result = await um.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("Failed to seed user: " +
                string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private async Task<(string FormToken, string Cookie)> GetAntiforgeryAsync(string url)
    {
        var resp = await Client.GetAsync(url, Xunit.TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET {url} must render so we can harvest its antiforgery token (got {(int)resp.StatusCode}).");

        var html = await resp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        var match = Regex.Match(
            html,
            @"name=""__RequestVerificationToken""[^>]*value=""(?<v>[^""]+)""",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
        if (!match.Success)
            throw new InvalidOperationException(
                $"No antiforgery token found in response from {url}.");
        var formToken = match.Groups["v"].Value;

        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
            throw new InvalidOperationException(
                $"GET {url} did not emit any Set-Cookie headers (antiforgery cookie required).");

        var antiforgeryCookie = setCookieValues
            .Select(h => h.Split(';', 2)[0])
            .FirstOrDefault(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "No .AspNetCore.Antiforgery.* cookie was set on the GET response.");

        return (formToken, antiforgeryCookie);
    }

    private async Task<HttpResponseMessage> PostFormWithAntiforgeryAsync(
        string url,
        string formToken,
        string antiforgeryCookie,
        IDictionary<string, string> fields)
    {
        var withToken = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = formToken,
        };
        using var content = new FormUrlEncodedContent(withToken);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        return await Client.SendAsync(req, Xunit.TestContext.Current.CancellationToken);
    }
}

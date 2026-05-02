using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// PR 5 of the email/OAuth decoupling sequence. Locks in the
/// <c>UpdateSource</c> attribution split for <c>GuestController.UpdatePreference</c>:
/// anonymous token-driven POSTs must record <c>"MagicLink"</c>, while
/// session-driven POSTs must record <c>"Guest"</c>. The previous in-flight
/// version of these tests went green on a no-op flip — the seed row already
/// matched the POST body, so <see cref="ICommunicationPreferenceService.UpdatePreferenceAsync(Guid, MessageCategory, bool, bool, string, System.Threading.CancellationToken)"/>
/// short-circuited as idempotent and the row stayed at <c>UpdateSource = "Default"</c>
/// while the controller still returned 200 OK. The fix is to drive the POST
/// against <see cref="MessageCategory.VolunteerUpdates"/> (default OptedOut=false)
/// with <c>emailEnabled=false</c>, which forces an actual write.
/// </summary>
public class UnsubscribeFlowTests : IntegrationTestBase
{
    public UnsubscribeFlowTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact(Timeout = 30_000)]
    public async Task TokenDriven_UpdatePreference_AttributesUpdateSourceToMagicLink()
    {
        // Seed a profileless user.
        Guid userId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            userId = await SeedBareUserAsync(scope, $"untoken-{Guid.NewGuid():N}@x.test");
        }

        // Generate an unsubscribe token for VolunteerUpdates (default OptedOut=false).
        // POSTing emailEnabled=false flips OptedOut → true, so the service writes
        // (instead of short-circuiting as idempotent and leaving the row at "Default").
        string token;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var commPrefService = scope.ServiceProvider.GetRequiredService<ICommunicationPreferenceService>();
            token = commPrefService.GenerateUnsubscribeToken(userId, MessageCategory.VolunteerUpdates);
        }

        // Use the per-test-class anonymous client (no session cookie). The GET with
        // utoken renders the page with an antiforgery field; the POST sends it back.
        var (formToken, cookie) = await GetAntiforgeryAsync(
            $"/Guest/CommunicationPreferences?utoken={Uri.EscapeDataString(token)}");

        var resp = await PostFormWithAntiforgeryAsync(
            "/Guest/CommunicationPreferences/Update",
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
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var pref = await db.Set<CommunicationPreference>()
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId && p.Category == MessageCategory.VolunteerUpdates);
        pref.OptedOut.Should().BeTrue("emailEnabled=false maps to OptedOut=true.");
        pref.UpdateSource.Should().Be("MagicLink",
            "anonymous token-driven updates must be attributed to MagicLink, " +
            "distinct from the seeded \"Default\" or session-driven \"Guest\".");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task SessionDriven_UpdatePreference_AttributesUpdateSourceToGuest()
    {
        // Sign in the per-test client. SignInAsFullyOnboardedAsync issues the
        // Identity cookie on the same HttpClient/CookieContainer used below.
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // No utoken → ResolveUserIdOrTokenAsync prefers the authenticated session,
        // which produces fromToken=false and source="Guest".
        var (formToken, cookie) = await GetAntiforgeryAsync("/Guest/CommunicationPreferences");

        var resp = await PostFormWithAntiforgeryAsync(
            "/Guest/CommunicationPreferences/Update",
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
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var pref = await db.Set<CommunicationPreference>()
            .AsNoTracking()
            .SingleAsync(p => p.UserId == userId && p.Category == MessageCategory.VolunteerUpdates);
        pref.OptedOut.Should().BeTrue("emailEnabled=false maps to OptedOut=true.");
        pref.UpdateSource.Should().Be("Guest",
            "session-driven updates must be attributed to Guest, distinct from MagicLink.");
    }

    // ---------------------------------------------------------------------
    // Helpers — mirror EmailGridFlowTests.
    // ---------------------------------------------------------------------

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
        var resp = await Client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET {url} must render so we can harvest its antiforgery token (got {(int)resp.StatusCode}).");

        var html = await resp.Content.ReadAsStringAsync();
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
        return await Client.SendAsync(req);
    }
}

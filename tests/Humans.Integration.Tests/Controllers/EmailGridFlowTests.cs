using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Data;
using Humans.Users.Domain;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// The emailed confirmation link through the real app: who may use it, what it merges, and which account the
/// browser ends up signed in to. The authorization tests are pure HTTP.
/// </summary>
public class EmailGridFlowTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60_000)]
    public async Task MailedLink_OtherAccountNeverFilledIn_MergesItIntoTheSignedInAccount()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var me = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var sharedEmail = $"shared-{Guid.NewGuid():N}@x.test";
        Guid shell;
        await using (var scope = Factory.Services.CreateAsyncScope())
            shell = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
        var link = await AddEmailAndGetLinkAsync(me, sharedEmail);

        // Opening the link changes nothing: mail scanners open links.
        var (formToken, cookie) = await GetAntiforgeryAsync(link.Url);
        (await MergedToAsync(shell)).Should().BeNull();

        var merge = await PostFormWithAntiforgeryAsync("/Users/Emails/Merge", formToken, cookie,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = link.Token });

        merge.StatusCode.Should().Be(HttpStatusCode.Found);
        (await MergedToAsync(shell)).Should().Be(me);
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        (await db.Set<UserEmail>().AsNoTracking().Where(e => e.Email == sharedEmail).ToListAsync(ct))
            .Should().ContainSingle().Which.Should().Match<UserEmail>(e => e.UserId == me && e.IsVerified);

        // The admin's look-back list reads it from the audit log.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var admin = await Client.GetAsync("/Users/Admin/AccountMerges", ct);
        var adminHtml = await admin.Content.ReadAsStringAsync(ct);
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        adminHtml.Should().Contain("Recently merged").And.Contain("Self-service").And.Contain(shell.ToString());
        adminHtml.Should().NotContain("<vc:", "an unbound view component ships as inert markup");
    }

    [HumansFact(Timeout = 60_000)]
    public async Task MailedLink_BothAccountsFilledIn_KeepsThePickedOneAndSignsInAsIt()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var me = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        var sharedEmail = $"shared-{Guid.NewGuid():N}@x.test";
        Guid other;
        await using (var scope = Factory.Services.CreateAsyncScope())
            other = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail, filledIn: true);
        var link = await AddEmailAndGetLinkAsync(me, sharedEmail);

        // First, because the antiforgery cookie is emitted on the first GET only.
        var (formToken, cookie) = await GetAntiforgeryAsync(link.Url);
        var page = await Client.GetAsync(link.Url, ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("name=\"keepUserId\"", "with two filled-in accounts the person picks which one is kept");
        html.Should().NotContain("<vc:", "an unbound view component ships as inert markup");

        // No pick, no merge.
        var unpicked = await PostFormWithAntiforgeryAsync("/Users/Emails/Merge", formToken, cookie,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = link.Token });
        unpicked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MergedToAsync(me)).Should().BeNull();
        (await MergedToAsync(other)).Should().BeNull();

        var merge = await PostFormWithAntiforgeryAsync("/Users/Emails/Merge", formToken, cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = link.Token,
                ["keepUserId"] = other.ToString(),
            });

        merge.StatusCode.Should().Be(HttpStatusCode.Found);
        (await MergedToAsync(me)).Should().Be(other);
        // The browser's own account is gone: it now carries the kept account's session.
        var emails = await Client.GetAsync("/Profile/Me/Emails", ct);
        emails.StatusCode.Should().Be(HttpStatusCode.OK);
        (await emails.Content.ReadAsStringAsync(ct)).Should().Contain(sharedEmail);
    }

    [HumansFact(Timeout = 60_000)]
    public async Task MailedLink_OnlyWorksForTheSignedInAccountThatAsked()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var sharedEmail = $"shared-{Guid.NewGuid():N}@x.test";
        Guid holder;
        Guid asking;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            holder = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            asking = await SeedBareUserAsync(scope, $"a-{Guid.NewGuid():N}@x.test");
        }
        var link = await AddEmailAndGetLinkAsync(asking, sharedEmail);

        // Not signed in: nothing but the sign-in page.
        using (var anonymous = Factory.CreateClient(new() { AllowAutoRedirect = false }))
        {
            var response = await anonymous.GetAsync(link.Url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.Headers.Location!.OriginalString.Should().Contain("/Account/Login");
        }

        // Signed in as somebody else: the page gives nothing away, and forcing the posts changes nothing.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);
        // First, because the antiforgery cookie is emitted on the first GET only.
        var (formToken, cookie) = await GetAntiforgeryAsync("/Profile/Me/Emails");
        var page = await Client.GetAsync(link.Url, ct);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(ct);
        html.Should().NotContain(sharedEmail).And.NotContain("/Users/Emails/Merge");

        foreach (var url in new[] { "/Users/Emails/Merge", "/Users/Emails/Confirm" })
        {
            var forced = await PostFormWithAntiforgeryAsync(url, formToken, cookie,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["token"] = link.Token,
                    ["keepUserId"] = asking.ToString(),
                });
            forced.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await MergedToAsync(holder)).Should().BeNull();
        (await MergedToAsync(asking)).Should().BeNull();
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        (await db.Set<UserEmail>().AsNoTracking().SingleAsync(e => e.UserId == asking && e.Email == sharedEmail, ct))
            .IsVerified.Should().BeFalse();
    }

    [HumansFact(Timeout = 30_000)]
    public async Task UnprivilegedUser_AdminPostToOtherUserEmails_ReturnsForbid()
    {
        Guid targetUserId;
        Guid targetEmailId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var email = $"target-{Guid.NewGuid():N}@x.test";
            targetUserId = await SeedUserWithVerifiedEmailAsync(scope, email);
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            targetEmailId = await db.Set<UserEmail>()
                .Where(e => e.UserId == targetUserId && e.Email == email)
                .Select(e => e.Id)
                .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        }

        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var (token, cookie) = await GetAntiforgeryAsync("/Profile/Me/Emails");

        // Cookie auth turns the controller's Forbid() into a 302 to AccessDenied.
        var setGoogleResp = await PostFormWithAntiforgeryAsync(
            $"/Google/Admin/{targetUserId}/WorkspaceEmail",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["emailId"] = targetEmailId.ToString(),
            });
        AssertForbidden(setGoogleResp);

        var unlinkResp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{targetUserId}/Admin/Emails/Unlink/{targetEmailId}",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal));
        AssertForbidden(unlinkResp);

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var row = await verifyDb.Set<UserEmail>().AsNoTracking().SingleAsync(e => e.Id == targetEmailId, Xunit.TestContext.Current.CancellationToken);
        row.Should().Match<UserEmail>(e => e.UserId == targetUserId && e.IsVerified);
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AdminManualVerify_AddressConfirmedOnAnotherAccount_StaysUnconfirmed()
    {
        // There is deliberately no admin "force": two accounts with one address are merged, never verified twice.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var sharedEmail = $"adminmerge-{Guid.NewGuid():N}@x.test";
        Guid targetUserId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            targetUserId = await SeedBareUserAsync(scope, $"target-{Guid.NewGuid():N}@x.test");
        }
        var link = await AddEmailAndGetLinkAsync(targetUserId, sharedEmail);
        var (token, cookie) = await GetAntiforgeryAsync($"/Profile/{targetUserId}/Admin/Emails");

        var resp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{targetUserId}/Admin/Emails/Verify", token, cookie,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["emailId"] = link.EmailId.ToString() });

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        (await db.Set<UserEmail>().AsNoTracking().SingleAsync(e => e.Id == link.EmailId, ct))
            .IsVerified.Should().BeFalse();
    }

    /// <summary>
    /// The manual-verify control is <c>AdminOnly</c> on a page HumanAdmin and Board also reach, and E2E has no Admin
    /// persona, so the positive case lives here; the negative half stays in Playwright.
    /// </summary>
    [HumansFact(Timeout = 30_000)]
    public async Task AdminManualVerify_MarksAPendingEmailVerified()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var pendingEmail = $"pending-{Guid.NewGuid():N}@x.test";
        Guid targetUserId;
        Guid emailId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            targetUserId = await SeedBareUserAsync(scope, $"verifytarget-{Guid.NewGuid():N}@x.test");

            // Added, not verified: the pending state the control exists for.
            var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
            var addResult = await userEmailService.AddEmailAsync(targetUserId, pendingEmail, ct);
            emailId = addResult.EmailId;
        }

        // The antiforgery cookie is emitted on the first GET only.
        var (token, cookie) = await GetAntiforgeryAsync($"/Profile/{targetUserId}/Admin/Emails");

        // Rendered only where the AdminOnly gate succeeds; a HumanAdmin reaches this page without it.
        var grid = await Client.GetAsync($"/Profile/{targetUserId}/Admin/Emails", ct);
        grid.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await grid.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Admin/Emails/Verify",
            "an Admin must be offered the manual-verify form on a pending row");

        var resp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{targetUserId}/Admin/Emails/Verify",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["emailId"] = emailId.ToString() });

        resp.StatusCode.Should().Be(HttpStatusCode.Found, "the action redirects back to the grid");
        resp.Headers.Location!.OriginalString.Should().NotContain("AccessDenied");

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var verified = await db.Set<UserEmail>()
            .AsNoTracking()
            .Where(e => e.Id == emailId)
            .Select(e => e.IsVerified)
            .SingleAsync(ct);
        verified.Should().BeTrue("the manual-verify POST must flip the pending row");
    }

    [HumansFact]
    public async Task AdminLinkRoute_DoesNotExist()
    {
        // Linking an OAuth identity is self-only: no admin route may create a Google link without the user's own OAuth
        // challenge.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        Guid otherUserId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            otherUserId = await SeedBareUserAsync(scope, $"linkroute-{Guid.NewGuid():N}@x.test");
        }

        var (token, cookie) = await GetAntiforgeryAsync("/Profile/Me/Emails");

        var resp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{otherUserId}/Admin/Emails/Link/Google",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal));

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    private async Task<(string Url, string Token, Guid EmailId)> AddEmailAndGetLinkAsync(Guid userId, string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<IUserEmailService>()
            .AddEmailAsync(userId, email, Xunit.TestContext.Current.CancellationToken);
        added.ErrorKey.Should().BeNull();
        return ($"/Users/Emails/Confirm?token={Uri.EscapeDataString(added.Token)}", added.Token, added.EmailId);
    }

    private async Task<Guid?> MergedToAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UsersDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.MergedToUserId)
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> SeedUserWithVerifiedEmailAsync(
        IServiceScope scope, string email, bool filledIn = false)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Seeded Human",
            BurnerName = filledIn ? "Seeded" : null,
            FirstName = filledIn ? "Seeded" : null,
            LastName = filledIn ? "Human" : null,
            Email = email,
            UserName = email,
            CreatedAt = now,
        };
        var result = await um.CreateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("Failed to seed user: " +
                string.Join("; ", result.Errors.Select(e => e.Description)));

        db.Set<UserEmail>().Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = email,
            IsVerified = true,
            IsPrimary = true,
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = now,
            UpdatedAt = now,
        });
        // Filled in: a profile with names.
        if (filledIn)
            db.Set<Profile>().Add(new Profile { Id = Guid.NewGuid(), UserId = user.Id, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return user.Id;
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
                $"No antiforgery token found in response from {url}. The page must contain a form with an antiforgery field.");
        var formToken = match.Groups["v"].Value;

        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
            throw new InvalidOperationException(
                $"GET {url} did not emit any Set-Cookie headers (antiforgery cookie required).");

        var antiforgeryCookie = setCookieValues
            .Select(h => h.Split(';', 2)[0])
            .FirstOrDefault(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));

        if (antiforgeryCookie is null)
            throw new InvalidOperationException(
                "No .AspNetCore.Antiforgery.* cookie was set on the GET response. " +
                "Was the GET successful and did the page emit an antiforgery token?");

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
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = content;
        // The CookieContainer would re-send it anyway; explicit is harmless.
        req.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        return await Client.SendAsync(req, Xunit.TestContext.Current.CancellationToken);
    }

    private static void AssertForbidden(HttpResponseMessage resp)
    {
        // Cookie auth maps Forbid() to a 302 to AccessDeniedPath; some configurations return 403, so accept either.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return;

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Contain("AccessDenied");
    }
}

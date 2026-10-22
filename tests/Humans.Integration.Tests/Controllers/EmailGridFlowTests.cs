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
/// The merge row is written by <see cref="IUserEmailService.VerifyEmailAsync"/>, not on add, so the merge tests drive
/// add then verify through the service. The authorization tests are pure HTTP.
/// </summary>
public class EmailGridFlowTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 30_000)]
    public async Task SelfAddEmail_AlreadyVerifiedOnAnotherUser_CreatesAccountMergeRequest_AndShowsMergePendingPill()
    {
        var sharedEmail = $"shared-{Guid.NewGuid():N}@x.test";
        Guid userBId;
        Guid userAId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            userBId = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            userAId = await SeedBareUserAsync(scope, $"a-{Guid.NewGuid():N}@x.test");
        }

        // Merge is written on verify.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
            var addResult = await userEmailService.AddEmailAsync(userAId, sharedEmail, Xunit.TestContext.Current.CancellationToken);
            addResult.IsConflict.Should().BeTrue(
                "AddEmail must flag the conflict so the verification email warns the user.");

            var verifyResult = await userEmailService.VerifyEmailAsync(userAId, addResult.EmailId, addResult.Token, Xunit.TestContext.Current.CancellationToken);
            verifyResult.MergeRequestCreated.Should().BeTrue(
                "the verify-time conflict path is what writes the merge request.");
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            var merge = await db.Set<AccountMergeRequest>()
                .AsNoTracking()
                .SingleAsync(m => m.TargetUserId == userAId && m.SourceUserId == userBId, Xunit.TestContext.Current.CancellationToken);

            merge.Status.Should().Be(AccountMergeRequestStatus.Pending);
            merge.Email.Should().Be(sharedEmail);
        }

        // The admin route renders the same view model, so the pill renders identically.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var resp = await Client.GetAsync($"/Profile/{userAId}/Admin/Emails", Xunit.TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);

        body.Should().ContainAny("Merge pending", "fa-code-merge");
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
        row.IsGoogle.Should().BeFalse("SetGoogle must not have run for the unprivileged actor.");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AdminAddEmail_AlreadyVerifiedOnAnotherUser_StillCreatesMergeRequest_NoForceAddBypass()
    {
        var sharedEmail = $"adminmerge-{Guid.NewGuid():N}@x.test";
        Guid sourceUserId;
        Guid targetUserId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            sourceUserId = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            targetUserId = await SeedBareUserAsync(scope, $"target-{Guid.NewGuid():N}@x.test");
        }

        // There is deliberately no admin "force fold": admin adds go through the same verify-then-merge flow.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
            var addResult = await userEmailService.AddEmailAsync(targetUserId, sharedEmail, Xunit.TestContext.Current.CancellationToken);
            addResult.IsConflict.Should().BeTrue();

            var verifyResult = await userEmailService.VerifyEmailAsync(targetUserId, addResult.EmailId, addResult.Token, Xunit.TestContext.Current.CancellationToken);
            verifyResult.MergeRequestCreated.Should().BeTrue(
                "even when an admin initiates the add, cross-user collisions must surface as merge requests, not silent folds.");
        }

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var merges = await db.Set<AccountMergeRequest>()
            .AsNoTracking()
            .Where(m => m.TargetUserId == targetUserId && m.SourceUserId == sourceUserId)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);
        merges.Should().ContainSingle();
        merges[0].Status.Should().Be(AccountMergeRequestStatus.Pending);
        merges[0].Email.Should().Be(sharedEmail);

        var sourceStillHasEmail = await db.Set<UserEmail>()
            .AsNoTracking()
            .AnyAsync(e => e.UserId == sourceUserId && e.Email == sharedEmail && e.IsVerified, Xunit.TestContext.Current.CancellationToken);
        sourceStillHasEmail.Should().BeTrue("merge has not been accepted yet — source data must be intact.");
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
            addResult.IsConflict.Should().BeFalse("the seeded address belongs to nobody else");
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

    private static async Task<Guid> SeedUserWithVerifiedEmailAsync(IServiceScope scope, string email)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Seeded Human",
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
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
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

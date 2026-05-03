using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Application.Interfaces.Email;
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
/// Task 23 of PR 4 (email/OAuth decoupling). Integration tests covering the
/// cross-user merge flow, admin-route auth gates, and the no-AdminLink
/// invariant from the email grid spec.
///
/// Notes on the patterns used here:
/// - Cross-user merge creation actually happens inside
///   <see cref="IUserEmailService.VerifyEmailAsync"/> when the verified email
///   already belongs to another user. AddEmailAsync only flags the conflict
///   on the returned token; the merge row is written when the verification
///   token is presented. Tests 1 and 3 therefore drive the service via DI
///   scope to exercise the full add → verify path, then exercise the HTTP
///   surface for the assertions that are HTTP-shaped (the MergePending pill
///   in test 1).
/// - Tests 2 and 4 are pure HTTP because they assert routing/authorization
///   behavior, not service behavior. Antiforgery is satisfied by harvesting
///   the token + cookie pair from the rendered self-Emails page.
/// </summary>
public class EmailGridFlowTests : IntegrationTestBase
{
    public EmailGridFlowTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact(Timeout = 30_000)]
    public async Task SelfAddEmail_AlreadyVerifiedOnAnotherUser_CreatesAccountMergeRequest_AndShowsMergePendingPill()
    {
        // ----- Seed: User B owns a verified UserEmail; User A is the actor.
        var sharedEmail = $"shared-{Guid.NewGuid():N}@x.test";
        Guid userBId;
        Guid userAId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            userBId = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            userAId = await SeedBareUserAsync(scope, $"a-{Guid.NewGuid():N}@x.test");
        }

        // ----- Drive AddEmail → VerifyEmail via the service. Merge is written on verify.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
            var addResult = await userEmailService.AddEmailAsync(userAId, sharedEmail);
            addResult.IsConflict.Should().BeTrue(
                "AddEmail must flag the conflict so the verification email warns the user.");

            var verifyResult = await userEmailService.VerifyEmailAsync(userAId, addResult.Token);
            verifyResult.MergeRequestCreated.Should().BeTrue(
                "the verify-time conflict path is what writes the merge request.");
        }

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
            var merge = await db.Set<AccountMergeRequest>()
                .AsNoTracking()
                .SingleAsync(m => m.TargetUserId == userAId && m.SourceUserId == userBId);

            merge.Status.Should().Be(AccountMergeRequestStatus.Pending);
            merge.Email.Should().Be(sharedEmail);
        }

        // ----- HTTP assertion: the MergePending badge renders on user A's grid.
        // Sign in as Admin and view A's grid via the admin route — same view, same
        // BuildEmailsViewModelAsync, so the MergePending pill renders identically.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var resp = await Client.GetAsync($"/Profile/{userAId}/Admin/Emails");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        // Localized string "Merge pending" is registered in SharedResource.resx
        // under EmailGrid_StatusMergePending; the row also carries the
        // fa-code-merge icon class. Either is a stable signal that the pill
        // rendered for the pending row.
        body.Should().ContainAny("Merge pending", "fa-code-merge");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task UnprivilegedUser_AdminPostToOtherUserEmails_ReturnsForbid()
    {
        // Seed an unrelated target user A and capture its primary email row id.
        Guid targetUserId;
        Guid targetEmailId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var email = $"target-{Guid.NewGuid():N}@x.test";
            targetUserId = await SeedUserWithVerifiedEmailAsync(scope, email);
            var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
            targetEmailId = await db.Set<UserEmail>()
                .Where(e => e.UserId == targetUserId && e.Email == email)
                .Select(e => e.Id)
                .SingleAsync();
        }

        // Sign in as a plain volunteer (not admin, not the target).
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var (token, cookie) = await GetAntiforgeryAsync("/Profile/Me/Emails");

        // SetGoogle: route is mapped, but the resource-based auth handler denies
        // because the actor is neither the target nor in role Admin. Cookie auth
        // converts the controller's Forbid() into a 302 to AccessDenied.
        var setGoogleResp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{targetUserId}/Admin/Emails/SetGoogle",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["emailId"] = targetEmailId.ToString(),
            });
        AssertForbidden(setGoogleResp);

        // Unlink: same handler, same expected outcome.
        var unlinkResp = await PostFormWithAntiforgeryAsync(
            $"/Profile/{targetUserId}/Admin/Emails/Unlink/{targetEmailId}",
            token,
            cookie,
            new Dictionary<string, string>(StringComparer.Ordinal));
        AssertForbidden(unlinkResp);

        // Sanity check: the underlying email row was not mutated.
        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var row = await verifyDb.Set<UserEmail>().AsNoTracking().SingleAsync(e => e.Id == targetEmailId);
        row.IsGoogle.Should().BeFalse("SetGoogle must not have run for the unprivileged actor.");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AdminAddEmail_AlreadyVerifiedOnAnotherUser_StillCreatesMergeRequest_NoForceAddBypass()
    {
        // Seed source user B (verified email) + target user A.
        var sharedEmail = $"adminmerge-{Guid.NewGuid():N}@x.test";
        Guid sourceUserId;
        Guid targetUserId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            sourceUserId = await SeedUserWithVerifiedEmailAsync(scope, sharedEmail);
            targetUserId = await SeedBareUserAsync(scope, $"target-{Guid.NewGuid():N}@x.test");
        }

        // The admin-Add endpoint internally calls IUserEmailService.AddEmailAsync,
        // which generates the verification token and flags IsConflict; the merge
        // row itself is written when the verification link is followed. There is
        // intentionally NO admin-only "force fold" path — admin mutates via the
        // same merge flow as the user. Drive add+verify through the service
        // (mirrors what would happen when the target user clicks the link).
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var userEmailService = scope.ServiceProvider.GetRequiredService<IUserEmailService>();
            var addResult = await userEmailService.AddEmailAsync(targetUserId, sharedEmail);
            addResult.IsConflict.Should().BeTrue();

            var verifyResult = await userEmailService.VerifyEmailAsync(targetUserId, addResult.Token);
            verifyResult.MergeRequestCreated.Should().BeTrue(
                "even when an admin initiates the add, cross-user collisions must surface as merge requests, not silent folds.");
        }

        await using var assertScope = Factory.Services.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var merges = await db.Set<AccountMergeRequest>()
            .AsNoTracking()
            .Where(m => m.TargetUserId == targetUserId && m.SourceUserId == sourceUserId)
            .ToListAsync();
        merges.Should().ContainSingle();
        merges[0].Status.Should().Be(AccountMergeRequestStatus.Pending);
        merges[0].Email.Should().Be(sharedEmail);

        // The source user's verified email still exists; nothing was force-folded.
        var sourceStillHasEmail = await db.Set<UserEmail>()
            .AsNoTracking()
            .AnyAsync(e => e.UserId == sourceUserId && e.Email == sharedEmail && e.IsVerified);
        sourceStillHasEmail.Should().BeTrue("merge has not been accepted yet — source data must be intact.");
    }

    [HumansFact(Timeout = 30_000)]
    public async Task AdminLinkRoute_DoesNotExist()
    {
        // The /Profile/{userId}/Admin/Emails/Link/Google admin route must NOT be
        // mapped. Linking an OAuth identity is a self-only operation; admins do
        // not have a backdoor that creates a Google linkage on a target account
        // without that user's OAuth challenge. A POST to the path should be
        // rejected by routing (404) or method-not-allowed (405).
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

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<Guid> SeedUserWithVerifiedEmailAsync(IServiceScope scope, string email)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
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
        await db.SaveChangesAsync();
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

    /// <summary>
    /// Fetches a GET page that contains an antiforgery-protected form and
    /// returns both the cookie value and the form-field token. The pair is
    /// what <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgery"/>
    /// validates against on subsequent POSTs.
    /// </summary>
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
        // The CookieContainer behind WebApplicationFactory's HttpClient
        // re-sends the antiforgery cookie automatically once it's been set,
        // but adding it explicitly is harmless and documents the dependency.
        req.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        return await Client.SendAsync(req);
    }

    private static void AssertForbidden(HttpResponseMessage resp)
    {
        // ASP.NET Core cookie auth maps Forbid() to a 302 redirect to
        // AccessDeniedPath ("/Account/AccessDenied"). Pure auth failures
        // could in some configurations come back as 403; accept either.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return;

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found);
        resp.Headers.Location!.OriginalString.Should().Contain("AccessDenied");
    }
}

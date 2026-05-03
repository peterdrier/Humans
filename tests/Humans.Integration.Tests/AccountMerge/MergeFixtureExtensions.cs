using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.AccountMerge;

/// <summary>
/// Extension entry points for seeding the source/target user pair plus
/// optional per-section data that the AccountMergeService.AcceptAsync fold
/// path needs to exercise. Phase 6.1 of the fold redesign — consumed by the
/// per-rule AcceptAsync tests in phase 6.2.
/// </summary>
public static class MergeFixtureExtensions
{
    /// <summary>
    /// Seeds a source User+Profile and a target User+Profile, then runs the
    /// supplied <paramref name="configure"/> action against a
    /// <see cref="MergeFixtureBuilder"/> so the test can layer per-section
    /// data on either side. All builder state is flushed in a single
    /// <c>SaveChangesAsync</c> at the end.
    /// </summary>
    public static async Task<(Guid sourceId, Guid targetId)> SeedMergeFixtureAsync(
        this HumansWebApplicationFactory fx,
        Action<MergeFixtureBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(fx);

        await using var scope = fx.Services.CreateAsyncScope();

        var sourceId = await SeedUserAndProfileAsync(scope, "Source");
        var targetId = await SeedUserAndProfileAsync(scope, "Target");

        var builder = new MergeFixtureBuilder(scope, sourceId, targetId);
        configure?.Invoke(builder);
        await builder.SaveAllAsync();

        return (sourceId, targetId);
    }

    /// <summary>
    /// Seeds a pending unverified <see cref="UserEmail"/> on the target user
    /// and a Pending <see cref="AccountMergeRequest"/> pointing at it, of the
    /// shape <c>AccountMergeService.AcceptAsync</c> expects to consume.
    /// Returns the merge-request id so callers can pass it to AcceptAsync.
    /// </summary>
    public static async Task<Guid> SeedMergeRequestAsync(
        this HumansWebApplicationFactory fx,
        Guid sourceUserId,
        Guid targetUserId,
        string? email = null)
    {
        // Generate a unique pending email per call so multiple tests in the
        // same fixture (shared Postgres container, shared DB) don't trip the
        // user_emails Email-uniqueness index.
        email ??= $"merge-target-{Guid.NewGuid():N}@example.com";

        ArgumentNullException.ThrowIfNull(fx);

        await using var scope = fx.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        // Pending (unverified) email on the target — production code creates
        // this when the user starts adding the conflicting address.
        var pendingEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = targetUserId,
            Email = email,
            IsVerified = false,
            IsPrimary = false,
            IsGoogle = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UserEmails.Add(pendingEmail);

        var request = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            SourceUserId = sourceUserId,
            TargetUserId = targetUserId,
            Email = email,
            PendingEmailId = pendingEmail.Id,
            Status = AccountMergeRequestStatus.Pending,
            CreatedAt = now,
        };
        db.AccountMergeRequests.Add(request);

        await db.SaveChangesAsync();
        return request.Id;
    }

    /// <summary>
    /// Mirror of the inline <c>SeedUserAsync</c> pattern in
    /// <c>CalendarServiceTests</c>: Identity-managed user creation plus a
    /// minimal Profile so cross-section seeders can attach
    /// <see cref="ContactField"/>, <see cref="ProfileLanguage"/>,
    /// <see cref="VolunteerHistoryEntry"/> rows directly.
    /// </summary>
    private static async Task<Guid> SeedUserAndProfileAsync(IServiceScope scope, string displayNameTag)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = $"{displayNameTag} {userId:N}".Substring(0, Math.Min(40, displayNameTag.Length + 33)),
            // Identity needs a non-null Email/UserName for CreateAsync; using a
            // unique-per-user synthetic address keeps the username uniqueness
            // check happy. Real verified emails attach via UserEmail rows.
            Email = $"merge-{userId:N}@test.local",
            UserName = $"merge-{userId:N}@test.local",
            CreatedAt = now,
        };

        var result = await um.CreateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to seed merge fixture user '{displayNameTag}': "
                + string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = displayNameTag,
            FirstName = displayNameTag,
            LastName = "Test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();

        return userId;
    }
}

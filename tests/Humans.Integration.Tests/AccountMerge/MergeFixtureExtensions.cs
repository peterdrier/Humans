using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Domain;

namespace Humans.Integration.Tests.AccountMerge;

public static class MergeFixtureExtensions
{
    /// <summary>All builder state is flushed in one <c>SaveChangesAsync</c> at the end.</summary>
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
    /// A pending unverified email on the target and a Pending request pointing at it, the shape <c>AcceptAsync</c>
    /// consumes.
    /// </summary>
    public static async Task<Guid> SeedMergeRequestAsync(
        this HumansWebApplicationFactory fx,
        Guid sourceUserId,
        Guid targetUserId,
        string? email = null)
    {
        email ??= $"merge-target-{Guid.NewGuid():N}@example.com";

        ArgumentNullException.ThrowIfNull(fx);

        await using var scope = fx.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        // Production creates this when the user starts adding the conflicting address.
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

        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return request.Id;
    }

    /// <summary><see cref="SeedMergeFixtureAsync"/> plus <see cref="SeedMergeRequestAsync"/>.</summary>
    public static async Task<(Guid requestId, Guid targetId, Guid sourceId)> SeedPendingMergeRequestAsync(
        this HumansWebApplicationFactory fx,
        string? email = null)
    {
        ArgumentNullException.ThrowIfNull(fx);
        var (sourceId, targetId) = await fx.SeedMergeFixtureAsync();
        var requestId = await fx.SeedMergeRequestAsync(sourceId, targetId, email);
        return (requestId, targetId, sourceId);
    }

    /// <summary>A minimal Profile so seeders can attach profile child rows directly.</summary>
    private static async Task<Guid> SeedUserAndProfileAsync(IServiceScope scope, string displayNameTag)
    {
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = $"{displayNameTag} {userId:N}".Substring(0, Math.Min(40, displayNameTag.Length + 33)),
            // Identity's CreateAsync needs a unique Email/UserName; real addresses attach as UserEmail rows.
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
        await db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return userId;
    }
}

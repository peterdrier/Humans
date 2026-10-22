using AwesomeAssertions;
using Humans.Base.Extensions;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Users.Tests.Repositories;

public sealed class UserRepositoryUserEmailTests : IDisposable
{
    [HumansTheory]
    [Xunit.InlineData("23505", "IX_user_emails_Email", true)]
    [Xunit.InlineData("23505", "PK_user_emails", false)]
    [Xunit.InlineData("23503", "IX_user_emails_Email", false)]
    public async Task ReconcilePlan_OnlyTranslatesVerifiedEmailConstraintRaces(
        string sqlState, string constraint, bool race)
    {
        var failure = new DbUpdateException("Save failed", new Npgsql.PostgresException(
            "Constraint violation", "ERROR", "ERROR", sqlState, constraintName: constraint));
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new FailedReconcileSave(failure)).Options;
        var repo = new UserRepository(new TestDbContextFactory(options));
        var row = new UserEmail { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = "name@example.test", IsVerified = true };
        Func<Task> reconcile = () => repo.ApplyUserEmailReconcilePlanAsync(null, null, null, row,
            Xunit.TestContext.Current.CancellationToken);

        if (race)
            (await reconcile.Should().ThrowAsync<OAuthReconcileConcurrencyException>()).Which.InnerException.Should().BeSameAs(failure);
        else
            (await reconcile.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure);
    }

    private sealed class FailedReconcileSave(DbUpdateException failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw failure;
    }

    private readonly UsersDbContext _dbContext;
    private readonly UserRepository _repo;

    public UserRepositoryUserEmailTests()
    {
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbContext = new UsersDbContext(options);
        _repo = new UserRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansTheory]
    [Xunit.InlineData("INDIGO@example.test", "indigo@example.test", true)]
    [Xunit.InlineData("First.Last+tag@GoogleMail.Com", "firstlast@gmail.com", true)]
    [Xunit.InlineData("firstlast@gmail.com", "FIRST.LAST+tag@googlemail.com", true)]
    [Xunit.InlineData("person+tag@example.test", "person@example.test", false)]
    [Xunit.InlineData("first.last@example.test", "firstlast@example.test", false)]
    [Xunit.InlineData("alex_smith@example.test", "alexXsmith@example.test", false)]
    [Xunit.InlineData("percent%name@example.test", "percentXname@example.test", false)]
    public async Task BatchLookup_UsesLiteralCaseInsensitiveAndGmailIdentityRules(
        string stored, string target, bool matches)
    {
        using var culture = new CultureScope("tr");
        var row = new UserEmail { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = stored };
        _dbContext.UserEmails.Add(row);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetUserEmailsByEmailsAsync([target], Xunit.TestContext.Current.CancellationToken);

        result.Select(email => email.Id).Should().BeEquivalentTo(matches ? [row.Id] : Array.Empty<Guid>());
    }

    [HumansFact]
    public async Task BatchLookup_ReturnsExactMatchesBeforeAliases_WithoutDuplicatingRows()
    {
        var alias = new UserEmail { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = "first.last+tag@gmail.com" };
        var exact = new UserEmail { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = "firstlast@gmail.com" };
        _dbContext.UserEmails.AddRange(alias, exact);
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetUserEmailsByEmailsAsync(
            ["firstlast@gmail.com", "FIRSTLAST@gmail.com"], Xunit.TestContext.Current.CancellationToken);

        result.Select(email => email.Id).Should().Equal(exact.Id, alias.Id);
    }
}

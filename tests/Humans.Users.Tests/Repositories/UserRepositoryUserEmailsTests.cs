using AwesomeAssertions;
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

    // The Gmail-alias fallback in GetUserEmailsByEmailsAsync uses EF.Functions.ILike, which InMemory
    // cannot evaluate; its identity key (GmailAliasEmailComparer) is covered in EmailNormalizationTests.
}

using Humans.MailerLite.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.MailerLite.Tests.Infrastructure;

/// <summary>The real <c>Repository</c> over a private in-memory store — one per call.</summary>
internal static class InMemoryMailerLiteRepository
{
    public static IMailerLiteRepository New() =>
        new Repository(
            new TestDbContextFactory<MailerLiteDbContext>(
                new DbContextOptionsBuilder<MailerLiteDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options),
            NullLogger<Repository>.Instance);
}

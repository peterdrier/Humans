using AwesomeAssertions;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Humans.Users.Contracts;
using Humans.Users.Data;

namespace Humans.Users.Tests.Entities;

public class UserEmailMappingTests
{
    [HumansFact]
    public void IsPrimary_IsMappedToLegacyIsNotificationTargetColumn()
    {
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(databaseName: nameof(IsPrimary_IsMappedToLegacyIsNotificationTargetColumn))
            .Options;

        using var ctx = new UsersDbContext(options);
        var entity = ctx.Model.FindEntityType(typeof(UserEmail))!;
        var prop = entity.FindProperty(nameof(UserEmail.IsPrimary))!;

        prop.GetColumnName().Should().Be("IsNotificationTarget",
            because: "PR 4 renames the C# property but the DB column stays — see " +
                     "architecture_dont_drop_columns_for_decoupling.");
    }
}

using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Testing;
using Microsoft.EntityFrameworkCore;

namespace Humans.Application.Tests.Entities;

public class UserEmailMappingTests
{
    [HumansFact]
    public void IsPrimary_IsMappedToLegacyIsNotificationTargetColumn()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(databaseName: nameof(IsPrimary_IsMappedToLegacyIsNotificationTargetColumn))
            .Options;

        using var ctx = new HumansDbContext(options);
        var entity = ctx.Model.FindEntityType(typeof(UserEmail))!;
        var prop = entity.FindProperty(nameof(UserEmail.IsPrimary))!;

        prop.GetColumnName().Should().Be("IsNotificationTarget",
            because: "PR 4 renames the C# property but the DB column stays — see " +
                     "architecture_dont_drop_columns_for_decoupling.");
    }
}

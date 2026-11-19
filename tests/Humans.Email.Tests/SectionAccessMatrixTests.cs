using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Email.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void Pins_the_EmailOutbox_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "EmailOutbox", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Email Outbox", 94));
        matrix.Roles.Should().Equal("Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Email outbox: Admin=Allowed");
    }
}

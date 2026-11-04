using AwesomeAssertions;
using NodaTime;
using Humans.Users.Contracts;
using Xunit;

namespace Humans.Users.Tests.Entities;

public class ProfileTests
{
    [HumansFact]
    public void FullName_ShouldCombineFirstAndLastName()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FirstName = "John",
            LastName = "Doe",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        };

        profile.FullName.Should().Be("John Doe");
    }

    [HumansFact]
    public void FullName_WithOnlyFirstName_ShouldReturnFirstName()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            FirstName = "John",
            LastName = "",
            CreatedAt = SystemClock.Instance.GetCurrentInstant(),
            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
        };

        profile.FullName.Should().Be("John");
    }
}

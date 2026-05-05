using AwesomeAssertions;
using Humans.Application.Helpers;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Tests.Helpers;

public class ProfileCompletionTests
{
    [HumansFact]
    public void NullProfile_Returns0()
    {
        ProfileCompletion.ComputePercent(null).Should().Be(0);
    }

    [HumansFact]
    public void EmptyProfile_Returns0()
    {
        var profile = new Profile { BurnerName = "x", FirstName = "y", LastName = "z" };

        ProfileCompletion.ComputePercent(profile).Should().Be(0);
    }

    [HumansFact]
    public void AllOptionalFieldsFilled_Returns100()
    {
        var profile = new Profile
        {
            BurnerName = "x",
            FirstName = "y",
            LastName = "z",
            City = "Madrid",
            CountryCode = "ES",
            Bio = "About me",
            Pronouns = "they/them",
            ContributionInterests = "art",
            DateOfBirth = new LocalDate(1990, 3, 15),
            EmergencyContactName = "Jane Doe",
            EmergencyContactPhone = "+34 600 000 000",
            EmergencyContactRelationship = "Partner",
        };

        ProfileCompletion.ComputePercent(profile).Should().Be(100);
    }
}

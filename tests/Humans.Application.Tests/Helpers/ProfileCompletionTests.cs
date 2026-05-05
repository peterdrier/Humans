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
        // Stub profile, nothing populated — score is 0 / 20.
        var profile = new Profile();

        ProfileCompletion.ComputePercent(profile).Should().Be(0);
    }

    [HumansFact]
    public void NamesOnly_AfterWidget_Returns15Percent()
    {
        // The onboarding-widget Names step fills BurnerName + FirstName +
        // LastName. That should land the user above zero on first arrival
        // at Home so the bar reflects what they've actually done.
        var profile = new Profile { BurnerName = "x", FirstName = "y", LastName = "z" };

        // 3 / 20 = 15
        ProfileCompletion.ComputePercent(profile).Should().Be(15);
    }

    [HumansFact]
    public void ProfilePicture_AddsAboutAQuarter()
    {
        var withoutPicture = new Profile { BurnerName = "x", FirstName = "y", LastName = "z" };
        var withPicture = new Profile
        {
            BurnerName = "x",
            FirstName = "y",
            LastName = "z",
            ProfilePictureData = new byte[] { 1, 2, 3 },
        };

        var delta = ProfileCompletion.ComputePercent(withPicture)
            - ProfileCompletion.ComputePercent(withoutPicture);

        // Picture is weight 5 of 20 = 25 percentage points.
        delta.Should().Be(25);
    }

    [HumansFact]
    public void NoPriorBurnExperience_CountsAsCvComplete()
    {
        // Setting the explicit "no prior burn" flag should be equivalent to
        // having at least one Burner CV entry — the user has answered the
        // question, which is what the bar is asking about.
        var profile = new Profile
        {
            BurnerName = "x",
            FirstName = "y",
            LastName = "z",
            NoPriorBurnExperience = true,
        };

        // 3 (names) + 2 (CV) = 5 / 20 = 25
        ProfileCompletion.ComputePercent(profile).Should().Be(25);
    }

    [HumansFact]
    public void EveryFieldPopulated_Returns100()
    {
        var profile = new Profile
        {
            BurnerName = "x",
            FirstName = "y",
            LastName = "z",
            ProfilePictureData = new byte[] { 1 },
            Pronouns = "they/them",
            Bio = "About me",
            City = "Madrid",
            CountryCode = "ES",
            DateOfBirth = new LocalDate(1990, 3, 15),
            ContributionInterests = "art",
            EmergencyContactName = "Jane Doe",
            EmergencyContactPhone = "+34 600 000 000",
            EmergencyContactRelationship = "Partner",
            NoPriorBurnExperience = true,
        };
        profile.Languages.Add(new ProfileLanguage { LanguageCode = "es" });

        ProfileCompletion.ComputePercent(profile, hasShiftTagPreferences: true).Should().Be(100);
    }
}

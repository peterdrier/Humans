using AwesomeAssertions;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Helpers;

public class ProfileCompletionTests
{
    [HumansFact]
    public void NullProfile_Returns0()
    {
        ProfileCompletion.ComputePercent(null, hasShiftTagPreferences: false).Should().Be(0);
    }

    [HumansFact]
    public void EmptyProfile_Returns0()
    {
        // Stub profile, nothing populated — score is 0 / 20.
        Percent(UserFixtures.Profile(), named: false).Should().Be(0);
    }

    [HumansFact]
    public void NamesOnly_AfterWidget_Returns15Percent()
    {
        // The onboarding-widget Names step fills BurnerName + FirstName +
        // LastName. That should land the user above zero on first arrival
        // at Home so the bar reflects what they've actually done.
        // 3 / 20 = 15
        Percent(Named()).Should().Be(15);
    }

    [HumansFact]
    public void ProfilePicture_AddsAboutAQuarter()
    {
        var withPicture = UserFixtures.Profile(profilePictureContentType: "image/png");

        var delta = Percent(withPicture) - Percent(Named());

        // Picture is weight 5 of 20 = 25 percentage points.
        delta.Should().Be(25);
    }

    [HumansFact]
    public void NoPriorBurnExperience_CountsAsCvComplete()
    {
        // Setting the explicit "no prior burn" flag should be equivalent to
        // having at least one Burner CV entry — the user has answered the
        // question, which is what the bar is asking about.
        var profile = UserFixtures.Profile(noPriorBurnExperience: true);

        // 3 (names) + 2 (CV) = 5 / 20 = 25
        Percent(profile).Should().Be(25);
    }

    [HumansFact]
    public void EveryFieldPopulated_Returns100()
    {
        var profile = UserFixtures.Profile(
            profilePictureContentType: "image/png",
            pronouns: "they/them",
            bio: "About me",
            city: "Madrid",
            countryCode: "ES",
            birthdayDay: 15,
            birthdayMonth: 3,
            contributionInterests: "art",
            emergencyContactName: "Jane Doe",
            emergencyContactPhone: "+34 600 000 000",
            emergencyContactRelationship: "Partner",
            noPriorBurnExperience: true,
            languages: [UserFixtures.Language("es")]);

        ProfileCompletion.ComputePercent(Info(profile), hasShiftTagPreferences: true).Should().Be(100);
    }

    private static ProfileInfo Named() =>
        UserFixtures.Profile();

    private static UserInfo Info(ProfileInfo profile, bool named = true) =>
        UserInfo.Create(
            named ? new User { BurnerName = "x", FirstName = "y", LastName = "z" } : new User(), [], [], profile);

    private static int Percent(ProfileInfo profile, bool named = true) =>
        ProfileCompletion.ComputePercent(Info(profile, named), hasShiftTagPreferences: false);
}

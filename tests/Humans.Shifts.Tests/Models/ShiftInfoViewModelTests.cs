using AwesomeAssertions;
using Humans.Shifts.Models;

namespace Humans.Shifts.Tests.Models;

public class ShiftInfoViewModelTests
{
    [HumansFact]
    public void TimePreferenceOptions_List_HasAllFourValues()
    {
        ShiftInfoViewModel.TimePreferenceOptions.Should()
            .BeEquivalentTo("Early Bird", "Night Owl", "All Day", "No Preference");
    }

    [HumansFact]
    public void ToggleQuirkOptions_List_ExcludesTimePreferences()
    {
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .BeEquivalentTo("Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights");

        // No overlap with time preferences
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .NotContain(ShiftInfoViewModel.TimePreferenceOptions);
    }

    [HumansFact]
    public void ExtractTimePreference_QuirksWithPreference_ReturnsIt()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().Be("Night Owl");
    }

    [HumansFact]
    public void ExtractTimePreference_NoPreference_ReturnsNull()
    {
        var quirks = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().BeNull();
    }

    [HumansFact]
    public void ExtractToggleQuirks_Quirks_ExcludeTimePreferences()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractToggleQuirks(quirks);

        result.Should().BeEquivalentTo("Sober Shift", "No Heights");
    }

    [HumansFact]
    public void MergeQuirks_PreferencePlusToggles_AreCombined()
    {
        var toggles = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.MergeQuirks("Early Bird", toggles);

        result.Should().BeEquivalentTo("Sober Shift", "No Heights", "Early Bird");
    }

    [HumansFact]
    public void MergeQuirks_NullPreference_ReturnsTogglesOnly()
    {
        var toggles = new List<string> { "Sober Shift" };

        var result = ShiftInfoViewModel.MergeQuirks(null, toggles);

        result.Should().BeEquivalentTo("Sober Shift");
    }

    [HumansFact]
    public void MergeSkills_UnknownExistingValues_AreKept()
    {
        var result = ShiftInfoViewModel.MergeSkills(
            ["Bartending", "Other"],
            "Rigging",
            ["Legacy Skill", "Other: Old", "Bartending"]);

        result.Should().BeEquivalentTo("Bartending", "Other: Rigging", "Legacy Skill");
    }

    [HumansFact]
    public void MergeLanguages_UnknownExistingValues_AreKept()
    {
        var result = ShiftInfoViewModel.MergeLanguages(
            ["English", "Other"],
            "Catalan",
            ["Legacy Language", "Other: Old", "English"]);

        result.Should().BeEquivalentTo("English", "Other: Catalan", "Legacy Language");
    }

    [HumansFact]
    public void MergePersistedQuirks_UnknownExistingValues_AreKept()
    {
        var result = ShiftInfoViewModel.MergePersistedQuirks(
            "Night Owl",
            ["Sober Shift"],
            ["Legacy Quirk", "Early Bird"]);

        result.Should().BeEquivalentTo("Sober Shift", "Night Owl", "Legacy Quirk");
    }
}

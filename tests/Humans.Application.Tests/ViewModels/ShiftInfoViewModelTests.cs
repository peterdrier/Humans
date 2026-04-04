using AwesomeAssertions;
using Humans.Web.Models;
using Xunit;

namespace Humans.Application.Tests.ViewModels;

public class ShiftInfoViewModelTests
{
    [Fact]
    public void TimePreferenceOptions_contains_all_four_values()
    {
        ShiftInfoViewModel.TimePreferenceOptions.Should()
            .BeEquivalentTo(["Early Bird", "Night Owl", "All Day", "No Preference"]);
    }

    [Fact]
    public void ToggleQuirkOptions_excludes_time_preferences()
    {
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .BeEquivalentTo(["Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights"]);

        // No overlap with time preferences
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .NotContain(ShiftInfoViewModel.TimePreferenceOptions);
    }

    [Fact]
    public void ExtractTimePreference_returns_matching_value_from_quirks()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().Be("Night Owl");
    }

    [Fact]
    public void ExtractTimePreference_returns_null_when_no_time_pref()
    {
        var quirks = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractToggleQuirks_excludes_time_preferences()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractToggleQuirks(quirks);

        result.Should().BeEquivalentTo(["Sober Shift", "No Heights"]);
    }

    [Fact]
    public void MergeQuirks_combines_time_pref_and_toggles()
    {
        var toggles = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.MergeQuirks("Early Bird", toggles);

        result.Should().BeEquivalentTo(["Sober Shift", "No Heights", "Early Bird"]);
    }

    [Fact]
    public void MergeQuirks_with_null_time_pref_returns_toggles_only()
    {
        var toggles = new List<string> { "Sober Shift" };

        var result = ShiftInfoViewModel.MergeQuirks(null, toggles);

        result.Should().BeEquivalentTo(["Sober Shift"]);
    }

    [Fact]
    public void MergeSkills_preserves_unknown_existing_values_while_updating_known_and_other_values()
    {
        var result = ShiftInfoViewModel.MergeSkills(
            ["Bartending", "Other"],
            "Rigging",
            ["Legacy Skill", "Other: Old", "Bartending"]);

        result.Should().BeEquivalentTo(["Bartending", "Other: Rigging", "Legacy Skill"]);
    }

    [Fact]
    public void MergeLanguages_preserves_unknown_existing_values_while_updating_known_and_other_values()
    {
        var result = ShiftInfoViewModel.MergeLanguages(
            ["English", "Other"],
            "Catalan",
            ["Legacy Language", "Other: Old", "English"]);

        result.Should().BeEquivalentTo(["English", "Other: Catalan", "Legacy Language"]);
    }

    [Fact]
    public void MergePersistedQuirks_preserves_unknown_existing_values()
    {
        var result = ShiftInfoViewModel.MergePersistedQuirks(
            "Night Owl",
            ["Sober Shift"],
            ["Legacy Quirk", "Early Bird"]);

        result.Should().BeEquivalentTo(["Sober Shift", "Night Owl", "Legacy Quirk"]);
    }
}

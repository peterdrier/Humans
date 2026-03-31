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
}

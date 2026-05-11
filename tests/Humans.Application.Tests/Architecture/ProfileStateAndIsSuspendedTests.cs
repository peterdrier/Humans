using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Testing;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Guards against accidental removal of the <see cref="Profile.State"/> property.
///
/// <para>
/// The "no new writers to <see cref="Profile.IsSuspended"/>" rule that used to
/// live alongside this test has migrated to the <c>HUM0004</c> analyzer
/// (<see cref="Humans.Analyzers.ProfileIsSuspendedWriteAnalyzer"/>). Analyzers
/// can fire on code that's present; they cannot assert that a symbol *is*
/// present, so this remaining guard stays as a reflection test.
/// </para>
/// </summary>
public class ProfileStateAndIsSuspendedTests
{
    [HumansFact]
    public void Profile_State_PropertyExists()
    {
        var stateProperty = typeof(Profile).GetProperty(nameof(Profile.State));
        stateProperty.Should().NotBeNull(
            because: "Issue #635 (§15i): Profile.State is the canonical lifecycle marker; " +
                     "removing it would silently break the lazy-backfill path.");
    }
}

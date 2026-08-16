using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Microsoft.Extensions.Localization;

namespace Humans.Gdpr.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Gdpr
/// (nobodies-collective/Humans#866, G5). The section had no architecture test file
/// before the move — its G0 audit recorded the missing invariants doc as predicate 7's
/// only gap and left the shape untested — so these are new with the project.
/// </summary>
public class GdprArchitectureTests
{

    [HumansFact]
    public void SectionHasNoControllers()
    {
        // The two download actions stay on Shell's ProfileController and GuestController —
        // moving either would be a URL change, out of a G5 move's scope. Stated as a test
        // rather than left to read as an oversight: this is why the project is plain
        // Microsoft.NET.Sdk, and adding a controller here means adding Sdk.Razor and a
        // Views/ tree at the same time.
        typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .Should().BeEmpty();
    }
}

namespace Humans.Testing;

/// <summary>
/// Humans.Integration.Tests runs only in the maintainer's local environment. In CI
/// (<c>CI</c>) and cloud agent runs (<c>CLAUDE_CODE_REMOTE</c>) its facts self-skip,
/// so a full-solution <c>dotnet test</c> stays green with no filter. A test that sets
/// its own <c>Skip</c>/<c>SkipUnless</c> (the localization sweep) replaces this gate.
/// <c>HUMANS_INTEGRATION_TESTS=1</c> forces the suite on anywhere.
/// </summary>
public static class IntegrationTestGate
{
    public static readonly string? SkipReason =
        !string.Equals(Environment.GetEnvironmentVariable("HUMANS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal)
        && (Environment.GetEnvironmentVariable("CI") is not null
            || Environment.GetEnvironmentVariable("CLAUDE_CODE_REMOTE") is not null)
            ? "Humans.Integration.Tests is local-only by design; skipped in CI/cloud — not a failure, not a finding (memory/process/integration-tests-are-not-ci-tests.md)."
            : null;

    public static bool AppliesTo(string? sourceFilePath) =>
        sourceFilePath?.Contains("Humans.Integration.Tests", StringComparison.Ordinal) == true;
}

namespace Humans.Testing;

/// <summary>
/// Humans.Integration.Tests runs only in the maintainer's local environment. In CI
/// (<c>CI</c>) and cloud agent runs (<c>CLAUDE_CODE_REMOTE</c>) its facts self-skip,
/// so a full-solution <c>dotnet test</c> stays green with no filter. A test that sets
/// its own <c>Skip</c>/<c>SkipUnless</c> (the localization sweep) replaces this gate.
/// <c>HUMANS_INTEGRATION_TESTS=1</c> forces the suite on anywhere.
/// </summary>
internal static class IntegrationTestGate
{
    internal static readonly string? SkipReason =
        !string.Equals(Environment.GetEnvironmentVariable("HUMANS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal)
        && (IsTrue("CI") || IsTrue("CLAUDE_CODE_REMOTE") || IsTrue("CODEX_CI"))
            ? "Humans.Integration.Tests is local-only by design; skipped in CI/cloud — not a failure, not a finding (memory/process/integration-tests-are-not-ci-tests.md)."
            : null;

    internal static bool AppliesTo(string? sourceFilePath) =>
        sourceFilePath?.Contains("Humans.Integration.Tests", StringComparison.Ordinal) == true;

    // An explicit CI=false / CLAUDE_CODE_REMOTE=false means "not that environment".
    private static bool IsTrue(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }
}

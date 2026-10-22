namespace Humans.Testing;

/// <summary>
/// Unless <c>HUMANS_INTEGRATION_TESTS=1</c>, integration facts self-skip and the Postgres fixture never starts, even
/// in IDE runners that ignore <c>IsTestProject=false</c>. A test's own <c>Skip</c>/<c>SkipUnless</c> replaces this gate.
/// </summary>
internal static class IntegrationTestGate
{
    internal static readonly string? SkipReason =
        !string.Equals(Environment.GetEnvironmentVariable("HUMANS_INTEGRATION_TESTS"), "1", StringComparison.Ordinal)
            ? "Humans.Integration.Tests is opt-in; set HUMANS_INTEGRATION_TESTS=1 to run it — not a failure, not a finding (memory/process/integration-tests-are-not-ci-tests.md)."
            : null;

    internal static bool AppliesTo(string? sourceFilePath) =>
        sourceFilePath?.Contains("Humans.Integration.Tests", StringComparison.Ordinal) == true;
}

namespace Humans.Testing;

/// <summary>
/// Humans.Integration.Tests is opt-in: unless <c>HUMANS_INTEGRATION_TESTS=1</c>, its
/// facts self-skip and its Postgres fixture never starts (covers IDE runners that
/// ignore the csproj's <c>IsTestProject=false</c>). A test that sets its own
/// <c>Skip</c>/<c>SkipUnless</c> (the localization sweep) replaces this gate.
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

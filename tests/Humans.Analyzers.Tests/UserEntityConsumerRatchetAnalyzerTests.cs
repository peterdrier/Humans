using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class UserEntityConsumerRatchetAnalyzerTests
{
    private const string Entities = """
        namespace Humans.Users.Contracts
        {
            public class User { }
            public class UserEmail { }
        }
        """;

    private const string Consumer = """
        namespace Demo
        {
            /// <summary>A <see cref="Humans.Users.Contracts.User"/> in a cref does not count.</summary>
            public class Consumer
            {
                public System.Collections.Generic.List<Humans.Users.Contracts.UserEmail> Emails { get; } = new();
                public string User { get; } = "";
            }
        }
        """;

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(
        string assembly, string source) =>
        AnalyzerTestHarness.RunAsync(new UserEntityConsumerRatchetAnalyzer(), assembly, source, referencedSource: Entities);

    [HumansFact]
    public async Task A_new_consumer_naming_an_entity_is_an_error()
    {
        var diagnostics = await RunAsync("Humans.Issues", Consumer);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be(UserEntityConsumerRatchetAnalyzer.DiagnosticId);
    }

    [HumansFact]
    public async Task Owners_and_baselined_consumers_are_silent()
    {
        (await RunAsync("Humans.Users", Consumer)).Should().BeEmpty();
        (await RunAsync("Humans.Teams", Consumer)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task A_baselined_assembly_that_no_longer_names_an_entity_is_stale()
    {
        var diagnostics = await RunAsync("Humans.Teams", "namespace Demo { public class Clean { } }");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be(UserEntityConsumerRatchetAnalyzer.StaleBaselineDiagnosticId);
    }
}

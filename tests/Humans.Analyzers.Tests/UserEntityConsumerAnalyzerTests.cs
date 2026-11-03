using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class UserEntityConsumerAnalyzerTests
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
        AnalyzerTestHarness.RunAsync(new UserEntityConsumerAnalyzer(), assembly, source, referencedSource: Entities);

    [HumansFact]
    public async Task A_new_consumer_naming_an_entity_is_an_error()
    {
        var diagnostics = await RunAsync("Humans.Issues", Consumer);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be(UserEntityConsumerAnalyzer.DiagnosticId);
    }

    [HumansFact]
    public async Task The_Users_section_and_Base_are_silent()
    {
        (await RunAsync("Humans.Users", Consumer)).Should().BeEmpty();
        (await RunAsync("Humans.Base", Consumer)).Should().BeEmpty();
    }
}

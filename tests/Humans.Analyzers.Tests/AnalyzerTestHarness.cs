using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Tests;

internal static class AnalyzerTestHarness
{
    private static readonly ImmutableArray<MetadataReference> BaseReferences = BuildBaseReferences();

    /// <summary>
    /// Compiles <paramref name="source"/> as a synthetic assembly named
    /// <paramref name="assemblyName"/>, then runs <paramref name="analyzer"/> against it.
    /// The name is documentation in most tests — no rule scopes itself by assembly any more.
    /// It is load-bearing only where a rule asks whether the compilation is a section, since
    /// <c>AssemblyScope.IsSection</c> looks up <c>&lt;assemblyName&gt;.Section</c>.
    /// </summary>
    /// <param name="referencedSource">
    /// Optional: compiled into its own assembly and added as a reference, rather than
    /// into <paramref name="source"/>'s own compilation. For rules that key off a
    /// symbol's own accessibility (e.g. HUM0034), a marker/base type the test needs to
    /// implement or derive from must live outside the assembly under test — exactly
    /// as it does in production (<c>ISection</c> in <c>Humans.Interfaces</c>,
    /// <c>Migration</c> in <c>Microsoft.EntityFrameworkCore</c>) — or the marker itself
    /// would also be a symbol declared in, and analyzed as part of, the compilation
    /// under test.
    /// </param>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        string source,
        string? referencedSource = null)
    {
        var references = BaseReferences;
        if (referencedSource is not null)
        {
            var referencedTree = CSharpSyntaxTree.ParseText(referencedSource, cancellationToken: Xunit.TestContext.Current.CancellationToken);
            var referencedCompilation = CSharpCompilation.Create(
                assemblyName: assemblyName + ".Stubs",
                syntaxTrees: [referencedTree],
                references: BaseReferences,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));
            references = references.Add(referencedCompilation.ToMetadataReference());
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(source, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var compileDiagnostics = compilation.GetDiagnostics(Xunit.TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (compileDiagnostics.Length > 0)
        {
            throw new InvalidOperationException(
                "Test snippet failed to compile: " +
                string.Join("; ", compileDiagnostics.Select(d => d.ToString())));
        }

        var withAnalyzers = compilation.WithAnalyzers([analyzer]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(Xunit.TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// No Humans.* assembly may be a reference of an analyzed compilation. The
    /// analyzer assembly carries linked copies of the architecture attributes
    /// (its name oracle — see nobodies-collective/Humans#1057), and the tests
    /// declare their own stubs for production markers (ISection, IRecurringJob,
    /// the attributes) — a real Humans assembly alongside them makes every such
    /// name ambiguous: CS0433 in the snippet, and <c>GetTypeByMetadataName</c>
    /// returning <c>null</c>, which quietly disables the rule under test.
    /// </summary>
    private static ImmutableArray<MetadataReference> BuildBaseReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length == 0)
                continue;
            if (Path.GetFileName(path).StartsWith("Humans.", StringComparison.OrdinalIgnoreCase))
                continue;

            builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }
}

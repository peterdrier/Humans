using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
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
    /// Tests typically use assembly names that match real production names
    /// ("Humans.Application", "Humans.Web", "Humans.Infrastructure", or any other name
    /// for the negative "scope excludes this assembly" cases) to exercise the
    /// AssemblyScope guard in each analyzer.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: new[] { syntaxTree },
            references: BaseReferences,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var compileDiagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
        if (compileDiagnostics.Length > 0)
        {
            throw new InvalidOperationException(
                "Test snippet failed to compile: " +
                string.Join("; ", compileDiagnostics.Select(d => d.ToString())));
        }

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
    }

    private static ImmutableArray<MetadataReference> BuildBaseReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (var path in trustedAssemblies.Split(System.IO.Path.PathSeparator))
        {
            if (path.Length > 0)
                builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }
}

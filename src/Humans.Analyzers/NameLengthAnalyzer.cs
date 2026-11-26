using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Humans.Analyzers;

/// <summary>
/// HUM0042 / HUM0043 / HUM0044 — method names stay short (design-rules.md, <i>Naming</i>).
/// A method name has at most six words and no "And"; a test name is <c>Method_Result</c> or
/// <c>Method_Condition_Result</c> with at most ten words and no "And" after the first part.
/// </summary>
/// <remarks>
/// Names that broke the rule when it arrived are listed, one <c>Type.Method</c> per line, in the project's
/// <c>NameBaseline.txt</c>. The list only shrinks: a line whose method is gone or now passes is HUM0044.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NameLengthAnalyzer : DiagnosticAnalyzer
{
    public const string MethodNameId = "HUM0042";
    public const string TestNameId = "HUM0043";
    public const string StaleBaselineId = "HUM0044";

    public const string BaselineFileName = "NameBaseline.txt";

    public const int MaxMethodWords = 6;
    public const int MaxTestWords = 10;
    public const int MaxTestParts = 3;

    public static readonly DiagnosticDescriptor MethodNameRule = new(
        id: MethodNameId,
        title: "Method name is too long or joins two actions",
        messageFormat: "Method name '{0}' {1}; say what it does in at most 6 words, and split a method that needs 'And'",
        category: AnalyzerCategories.Naming,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A method name is a short verb phrase: at most six words, not counting Async, and no 'And'.");

    public static readonly DiagnosticDescriptor TestNameRule = new(
        id: TestNameId,
        title: "Test name is not a short Method_Condition_Result",
        messageFormat: "Test name '{0}' {1}; name the one rule it proves as Method_Result or Method_Condition_Result in at most 10 words",
        category: AnalyzerCategories.Naming,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A test name is Method_Result or Method_Condition_Result in PascalCase: two or three parts, at most " +
            "ten words, no 'And' after the first part. The asserts list the details; a why goes in a comment.");

    public static readonly DiagnosticDescriptor StaleBaselineRule = new(
        id: StaleBaselineId,
        title: "NameBaseline.txt lists a name that no longer breaks the naming rule",
        messageFormat: "'{0}' no longer breaks the naming rule; delete its line from NameBaseline.txt",
        category: AnalyzerCategories.Naming,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The baseline of old names only shrinks.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [MethodNameRule, TestNameRule, StaleBaselineRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var baselineFile = context.Options.AdditionalFiles.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f.Path), BaselineFileName, StringComparison.Ordinal));
        var baseline = ReadBaseline(baselineFile?.GetText(context.CancellationToken));
        var used = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        context.RegisterSymbolAction(
            ctx =>
            {
                var method = (IMethodSymbol)ctx.Symbol;
                if (!IsOwnDeclaration(method))
                    return;

                var isTest = IsTest(method);
                var problem = isTest ? TestProblem(method.Name) : MethodProblem(method.Name);
                if (problem is null)
                    return;

                var key = method.ContainingType.ToDisplayString() + "." + method.Name;
                if (baseline.ContainsKey(key))
                {
                    used.TryAdd(key, 0);
                    return;
                }

                ctx.ReportDiagnostic(Diagnostic.Create(
                    isTest ? TestNameRule : MethodNameRule, method.Locations[0], method.Name, problem));
            },
            SymbolKind.Method);

        if (baselineFile is null)
            return;

        context.RegisterCompilationEndAction(ctx =>
        {
            foreach (var entry in baseline.Where(e => !used.ContainsKey(e.Key)))
            {
                var location = Location.Create(baselineFile.Path, entry.Value.Span, entry.Value.Lines);
                ctx.ReportDiagnostic(Diagnostic.Create(StaleBaselineRule, location, entry.Key));
            }
        });
    }

    private static Dictionary<string, (TextSpan Span, LinePositionSpan Lines)> ReadBaseline(SourceText? text)
    {
        var entries = new Dictionary<string, (TextSpan, LinePositionSpan)>(StringComparer.Ordinal);
        if (text is null)
            return entries;

        foreach (var line in text.Lines)
        {
            var value = line.ToString().Trim();
            if (value.Length > 0 && value[0] != '#')
                entries[value] = (line.Span, text.Lines.GetLinePositionSpan(line.Span));
        }

        return entries;
    }

    /// <summary>
    /// Only a name this code chose, and only once: an override, an interface implementation and a controller
    /// action (its name is the URL) are named elsewhere.
    /// </summary>
    private static bool IsOwnDeclaration(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsImplicitlyDeclared || method.IsOverride
            || method.ContainingType is null || method.Locations.Length == 0 || !method.Locations[0].IsInSource)
            return false;

        if (method.DeclaredAccessibility == Accessibility.Public && InheritsFrom(method.ContainingType, "ControllerBase"))
            return false;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(member), method))
                    return false;
            }
        }

        return true;
    }

    private static bool IsTest(IMethodSymbol method) =>
        method.GetAttributes().Any(a => a.AttributeClass is { } type
            && (string.Equals(type.Name, "FactAttribute", StringComparison.Ordinal) || InheritsFrom(type, "FactAttribute")));

    private static bool InheritsFrom(INamedTypeSymbol type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, baseName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? MethodProblem(string name)
    {
        var words = Words(name);
        if (words.Count > 0 && string.Equals(words[words.Count - 1], "Async", StringComparison.Ordinal))
            words.RemoveAt(words.Count - 1);

        if (words.Count > MaxMethodWords)
            return $"has {words.Count} words";

        return words.Contains("And", StringComparer.Ordinal) ? "contains 'And'" : null;
    }

    private static string? TestProblem(string name)
    {
        var parts = name.Split('_');
        if (parts.Any(p => p.Length > 0 && char.IsLower(p[0])))
            return "is a lowercase sentence";

        if (parts.Length < 2)
            return "has no result part";

        if (parts.Any(p => p.Length == 0))
            return "has an empty part";

        if (parts.Length > MaxTestParts)
            return $"has {parts.Length} parts";

        var count = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var words = Words(parts[i]);
            if (i > 0 && words.Contains("And", StringComparer.Ordinal))
                return "contains 'And'";

            count += words.Count(w => !string.Equals(w, "Async", StringComparison.Ordinal));
        }

        return count > MaxTestWords ? $"has {count} words" : null;
    }

    /// <summary>
    /// Splits an identifier into words at underscores, at a lower-to-upper step, at the end of an acronym
    /// (<c>XMLParser</c> is XML + Parser) and around digit runs.
    /// </summary>
    internal static List<string> Words(string name)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && !(i > start && StartsWord(name, i)) && name[i] != '_')
                continue;

            if (i > start)
                words.Add(name.Substring(start, i - start));

            start = i < name.Length && name[i] == '_' ? i + 1 : i;
        }

        return words;
    }

    private static bool StartsWord(string name, int i)
    {
        char previous = name[i - 1], current = name[i];
        if (char.IsDigit(current))
            return !char.IsDigit(previous);
        if (char.IsDigit(previous))
            return true;
        if (!char.IsUpper(current))
            return false;

        return !char.IsUpper(previous) || (i + 1 < name.Length && char.IsLower(name[i + 1]));
    }
}

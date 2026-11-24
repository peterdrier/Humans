namespace Humans.Analyzers;

internal static class AnalyzerCategories
{
    public const string Architecture = "Humans.Architecture";

    /// <summary>The only category that also runs on test projects; <c>.editorconfig</c> switches <see cref="Architecture"/> off there.</summary>
    public const string Naming = "Humans.Naming";
}

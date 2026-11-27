namespace Humans.Analyzers;

internal static class AnalyzerCategories
{
    public const string Architecture = "Humans.Architecture";

    /// <summary>Runs on test projects too; <c>.editorconfig</c> switches only <see cref="Architecture"/> off there.</summary>
    public const string Naming = "Humans.Naming";

    /// <summary>Code-shape rules that hold for production and tests alike (HUM0046); runs on test projects like <see cref="Naming"/>.</summary>
    public const string Structure = "Humans.Structure";
}

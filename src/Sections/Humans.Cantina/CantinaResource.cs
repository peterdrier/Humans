namespace Humans.Cantina;

/// <summary>
/// Must stay <c>namespace Humans.Cantina</c>: the SDK derives the adjacent .resx manifest name from this file's
/// namespace, and any other namespace makes every Cantina string fall back to its raw key.
/// </summary>
/// <remarks>Public because the boot localization diagnostic finds markers via <c>GetExportedTypes()</c>.</remarks>
public class CantinaResource;

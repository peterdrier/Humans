namespace Humans.Search;

/// <summary>
/// Marker type for Search's resource set — every key that only <c>/Search</c> renders.
/// The <c>.resx</c> files sit beside this file and the manifest name derives from this
/// type's namespace, so it must stay <c>namespace Humans.Search</c>; anything else makes every
/// Search string fall back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>. Other <c>Search_</c>-prefixed keys live in <c>SharedResource</c>
/// because their renderers are elsewhere (<c>Docs/Search.md</c>, Resource set).
/// </remarks>
public class SearchResource;
